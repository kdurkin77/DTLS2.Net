﻿/***********************************************************************************************************************
 Copyright (c) 2016, Imagination Technologies Limited and/or its affiliated group companies.
 All rights reserved.

 Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
 following conditions are met:
     1. Redistributions of source code must retain the above copyright notice, this list of conditions and the
        following disclaimer.
     2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the
        following disclaimer in the documentation and/or other materials provided with the distribution.
     3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote
        products derived from this software without specific prior written permission.

 THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
 INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE 
 USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
***********************************************************************************************************************/

using DTLS.Net.Util;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Security;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DTLS
{
    internal static class TLSUtils
    {
        public static DateTime UnixEpoch = new(1970, 1, 1);

        private static readonly byte[] _MASTER_SECRET_LABEL = Encoding.ASCII.GetBytes("master secret");
        private const int _MASTERSECRETLENGTH = 48;
        private static readonly TlsCipherFactory _CipherFactory = new DefaultTlsCipherFactory();

        public static byte[] CalculateMasterSecret(byte[] preMasterSecret, IKeyExchange keyExchange)
        {
            if (preMasterSecret == null)
            {
                throw new ArgumentNullException(nameof(preMasterSecret));
            }

            if (keyExchange == null)
            {
                throw new ArgumentNullException(nameof(keyExchange));
            }

            var seed = _MASTER_SECRET_LABEL
                .Concat(keyExchange.ClientRandom.Serialise())
                .Concat(keyExchange.ServerRandom.Serialise())
                .ToArray();

            var result = PseudorandomFunction(preMasterSecret, seed, _MASTERSECRETLENGTH);
            Array.Clear(preMasterSecret, 0, preMasterSecret.Length);
            return result;
        }

        public static byte[] PseudorandomFunction(byte[] secret, byte[] seed, int length)
        {
            if (secret == null)
            {
                throw new ArgumentNullException(nameof(secret));
            }

            if (seed == null)
            {
                throw new ArgumentNullException(nameof(seed));
            }

            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            var result = new byte[length];
            using (var hmac = new HMACSHA256(secret))
            {
                var iterations = (int)Math.Ceiling(length / (double)hmac.HashSize);
                //var dataToHash = seed;
                var offset = 0;
                for (var index = 0; index < iterations; index++)
                {
                    //dataToHash = hmac.ComputeHash(dataToHash);
                    //hmac.TransformBlock(dataToHash, 0, dataToHash.Length, dataToHash, 0);
                    //var hash = hmac.TransformFinalBlock(seed, 0, seed.Length);
                    var hash = hmac.ComputeHash(seed);
                    Buffer.BlockCopy(hash, 0, result, offset, Math.Min(hash.Length, length - offset));
                    offset += hash.Length;
                }
                return result;
            }
        }

        private static int GetEncryptionAlgorithm(TCipherSuite cipherSuite)
        {
            if (cipherSuite == TCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CCM_8)
            {
                return EncryptionAlgorithm.AES_128_CCM_8;
            }

            if (cipherSuite == TCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256)
            {
                return EncryptionAlgorithm.AES_128_CBC;
            }

            if (cipherSuite == TCipherSuite.TLS_PSK_WITH_AES_128_CCM_8)
            {
                return EncryptionAlgorithm.AES_128_CCM_8;
            }

            if (cipherSuite == TCipherSuite.TLS_PSK_WITH_AES_128_CBC_SHA256)
            {
                return EncryptionAlgorithm.AES_128_CBC;
            }

            if (cipherSuite == TCipherSuite.TLS_ECDHE_PSK_WITH_AES_128_CBC_SHA256)
            {
                return EncryptionAlgorithm.AES_128_CBC;
            }

            if (cipherSuite == TCipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA)
            {
                return EncryptionAlgorithm.AES_256_CBC;
            }

            return 0;
        }

        private static int GetMACAlgorithm(TCipherSuite cipherSuite)
        {
            if (cipherSuite == TCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CCM_8)
            {
                return MacAlgorithm.cls_null;
            }

            if (cipherSuite == TCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256)
            {
                return MacAlgorithm.hmac_sha256;
            }

            if (cipherSuite == TCipherSuite.TLS_PSK_WITH_AES_128_CCM_8)
            {
                return MacAlgorithm.cls_null;
            }

            if (cipherSuite == TCipherSuite.TLS_PSK_WITH_AES_128_CBC_SHA256)
            {
                return MacAlgorithm.hmac_sha256;
            }

            if (cipherSuite == TCipherSuite.TLS_ECDHE_PSK_WITH_AES_128_CBC_SHA256)
            {
                return MacAlgorithm.hmac_sha256;
            }

            if (cipherSuite == TCipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA)
            {
                return MacAlgorithm.hmac_sha1;
            }

            return 0;
        }

        public static TlsCipher AssignCipher(byte[] preMasterSecret, bool client, Version version, HandshakeInfo handshakeInfo)
        {
            if (preMasterSecret == null)
            {
                throw new ArgumentNullException(nameof(preMasterSecret));
            }

            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            if (handshakeInfo == null)
            {
                throw new ArgumentNullException(nameof(handshakeInfo));
            }

            TlsContext context = new DTLSContext(client, version, handshakeInfo);
            var securityParameters = context.SecurityParameters;
            var isExtendedMasterSecret = handshakeInfo.Extensions.Exists(e => e.ExtensionType == TExtensionType.ExtendedMasterSecret);
            var seed =
                isExtendedMasterSecret
                ? handshakeInfo.GetHash(version)
                : [.. securityParameters.ClientRandom, .. securityParameters.ServerRandom];
            var asciiLabel =
                isExtendedMasterSecret
                ? ExporterLabel.extended_master_secret
                : ExporterLabel.master_secret;

            handshakeInfo.MasterSecret = TlsUtilities.IsTlsV11(context) ?
                TlsUtilities.PRF_legacy(preMasterSecret, asciiLabel, seed, 48)
                : TlsUtilities.PRF(context, preMasterSecret, asciiLabel, seed, 48);

            //seed =
            //    isExtendedMasterSecret
            //    ? handshakeInfo.GetHash(version)
            //    : [.. securityParameters.ServerRandom, .. securityParameters.ClientRandom];
            //var key_block = TlsUtilities.IsTlsV11(context) ?
            //    TlsUtilities.PRF_legacy(handshakeInfo.MasterSecret, ExporterLabel.key_expansion, seed, 96)
            //    : TlsUtilities.PRF(context, handshakeInfo.MasterSecret, ExporterLabel.key_expansion, seed, 96);

            return _CipherFactory
                .CreateCipher(context, GetEncryptionAlgorithm(handshakeInfo.CipherSuite), GetMACAlgorithm(handshakeInfo.CipherSuite));
        }

        public static byte[] CalculateKeyBlock(TlsContext context, int size)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (size < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }

            var securityParameters = context.SecurityParameters;
            var master_secret = securityParameters.MasterSecret;
            var seed = securityParameters.ServerRandom.Concat(securityParameters.ClientRandom).ToArray();

            return TlsUtilities.IsTlsV11(context)
                ? TlsUtilities.PRF_legacy(master_secret, ExporterLabel.key_expansion, seed, size)
                : TlsUtilities.PRF(context, master_secret, ExporterLabel.key_expansion, seed, size);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Other methods are available but RSA is just for windows")]
        public static byte[] Sign(AsymmetricKeyParameter privateKey, CngKey cngKey, bool client, Version version, HandshakeInfo handshakeInfo,
            SignatureHashAlgorithm signatureHashAlgorithm, byte[] hash)
        {
            if (privateKey == null && cngKey == null)
            {
                throw new ArgumentException("No key or Rsa CSP provided");
            }

            if (privateKey == null)
            {

                if (signatureHashAlgorithm.Signature == TSignatureAlgorithm.RSA)
                {
                    return NCryptInterop.SignHashRaw(cngKey, hash, cngKey.KeySize);
                }

                throw new ArgumentException("Need private key for non-RSA Algorithms");
            }

            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            if (handshakeInfo == null)
            {
                throw new ArgumentNullException(nameof(handshakeInfo));
            }

            if (signatureHashAlgorithm == null)
            {
                throw new ArgumentNullException(nameof(signatureHashAlgorithm));
            }

            if (hash == null)
            {
                throw new ArgumentNullException(nameof(hash));
            }

            TlsSigner signer = null;
            switch (signatureHashAlgorithm.Signature)
            {
                case TSignatureAlgorithm.Anonymous:
                    break;
                case TSignatureAlgorithm.RSA:
                    signer = new TlsRsaSigner();
                    break;
                case TSignatureAlgorithm.DSA:
                    signer = new TlsDssSigner();
                    break;
                case TSignatureAlgorithm.ECDSA:

                    signer = new TlsECDsaSigner();
                    break;
                default:
                    break;
            }

            var context = new DTLSContext(client, version, handshakeInfo);
            var randomGenerator = new CryptoApiRandomGenerator();
            context.SecureRandom = new SecureRandom(randomGenerator);

            signer.Init(context);
            if (TlsUtilities.IsTlsV12(context))
            {
                var signatureAndHashAlgorithm = new SignatureAndHashAlgorithm((byte)signatureHashAlgorithm.Hash, (byte)signatureHashAlgorithm.Signature);
                return signer.GenerateRawSignature(signatureAndHashAlgorithm, privateKey, hash);
            }
            else
            {
                return signer.GenerateRawSignature(privateKey, hash);
            }
        }

        public static byte[] GetVerifyData(Version version, HandshakeInfo handshakeInfo, bool client, bool isClientFinished,
            byte[] handshakeHash)
        {
            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            if (handshakeInfo == null)
            {
                throw new ArgumentNullException(nameof(handshakeInfo));
            }

            if (handshakeHash == null)
            {
                throw new ArgumentNullException(nameof(handshakeHash));
            }

            TlsContext context = new DTLSContext(client, version, handshakeInfo);
            var asciiLabel = isClientFinished ? ExporterLabel.client_finished : ExporterLabel.server_finished;
            return TlsUtilities.IsTlsV11(context) ?
                TlsUtilities.PRF_legacy(handshakeInfo.MasterSecret, asciiLabel, handshakeHash, 12)
                : TlsUtilities.PRF(context, handshakeInfo.MasterSecret, asciiLabel, handshakeHash, 12);
        }

        internal static byte[] GetPSKPreMasterSecret(byte[] otherSecret, byte[] psk)
        {
            if (otherSecret == null)
            {
                throw new ArgumentNullException(nameof(otherSecret));
            }

            if (psk == null)
            {
                throw new ArgumentNullException(nameof(psk));
            }

            var result = new byte[4 + otherSecret.Length + psk.Length];
            NetworkByteOrderConverter.WriteUInt16(result, 0, (ushort)otherSecret.Length);
            Buffer.BlockCopy(otherSecret, 0, result, 2, otherSecret.Length);
            NetworkByteOrderConverter.WriteUInt16(result, (uint)(2 + otherSecret.Length), (ushort)psk.Length);
            Buffer.BlockCopy(psk, 0, result, 4 + otherSecret.Length, psk.Length);
            return result;
        }

        internal static byte[] GetRsaPreMasterSecret(Version version)
        {
            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            using (var random = RandomNumberGenerator.Create())
            {
                var randomData = new byte[46];
                random.GetBytes(randomData);
                var versionBytes = new byte[] { (byte)(255 - version.Major), (byte)(255 - version.Minor) };
                return [.. versionBytes, .. randomData];
            }
        }

        internal static byte[] GetEncryptedRsaPreMasterSecret(byte[] cert, byte[] premaster)
        {
            if (cert == null)
            {
                throw new ArgumentNullException(nameof(cert));
            }

            if (premaster == null)
            {
                throw new ArgumentNullException(nameof(premaster));
            }


            var certificate = new X509Certificate2(cert);
            var rsa = certificate.GetRSAPublicKey();
            return rsa.Encrypt(premaster, RSAEncryptionPadding.Pkcs1);
        }
    }
}

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

using DTLS.Net;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Utilities.IO.Pem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
#if !NET452 && !NET47
using System.Runtime.InteropServices;
#endif
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace DTLS
{
    public class Client : IDisposable
    {
        private static readonly Version _SupportedVersion = DTLSRecord.Version1_2;
        private readonly HandshakeInfo _HandshakeInfo = new HandshakeInfo();
        private readonly DTLSRecords _Records = new DTLSRecords();
        private readonly List<byte[]> _FragmentedRecordList = new List<byte[]>();
        private readonly CancellationTokenSource _Cts = new CancellationTokenSource();

        //The maximum safe UDP payload is 508 bytes. Except on an IPv6-only route, where the maximum payload is 1,212 bytes.
        //https://stackoverflow.com/questions/1098897/what-is-the-largest-safe-udp-packet-size-on-the-internet#:~:text=The%20maximum%20safe%20UDP%20payload%20is%20508%20bytes.&text=Except%20on%20an%20IPv6%2Donly,bytes%20may%20be%20preferred%20instead.
        private static int _MaxPacketSize = 1212;
        private Task _ReceiveTask;
        private Task _ProcessRecordTask;
        private Action<EndPoint, byte[]> _DataReceivedFunction;
        private Socket _Socket;
        private bool _Terminate;
        private EndPoint _ServerEndPoint;
        private ushort? _ServerEpoch;
        private long _ServerSequenceNumber;
        private ushort? _EncyptedServerEpoch;
        private ushort _Epoch;
        private ushort _MessageSequence;
        private TlsCipher _Cipher;
        private Version _Version = _SupportedVersion;
        private bool _SendCertificate;
        private IHandshakeMessage _ClientKeyExchange;
        private Certificate _Certificate;
        private AsymmetricKeyParameter _PrivateKey;
        private PSKIdentity _PSKIdentity;

        private byte[] _ReceivedData = new byte[0];
        private byte[] _RecvDataBuffer = new byte[0];
        private bool _IsFragment = false;
        private bool _ConnectionComplete = false;
        private bool _Disposed = false;
        private long _SequenceNumber = -1; //realy only 48 bit

        public EndPoint LocalEndPoint { get; }

        public PSKIdentities PSKIdentities { get; }

        public List<TCipherSuite> SupportedCipherSuites { get; }
        public byte[] ServerCertificate { get; set; }

#if NETSTANDARD2_1 || NETSTANDARD2_0
        private CngKey _PrivateKeyRsa;
        public CngKey PublicKey { get; set; }
#else
        private RSACryptoServiceProvider _PrivateKeyRsa;
        public RSACryptoServiceProvider PublicKey { get; set; }
#endif

        public Client(EndPoint localEndPoint)
            : this(localEndPoint, new List<TCipherSuite>())
        {
            this.SupportedCipherSuites = new List<TCipherSuite>();
            if(localEndPoint.AddressFamily != AddressFamily.InterNetworkV6)
            {
                _MaxPacketSize = 508;
            }
        }

        public Client(EndPoint localEndPoint, List<TCipherSuite> supportedCipherSuites)
        {
            this.LocalEndPoint = localEndPoint ?? throw new ArgumentNullException(nameof(localEndPoint));
            this.SupportedCipherSuites = supportedCipherSuites ?? throw new ArgumentNullException(nameof(supportedCipherSuites));
            this.PSKIdentities = new PSKIdentities();
            this._HandshakeInfo.ClientRandom = new RandomData();
            this._HandshakeInfo.ClientRandom.Generate();
        }
        
        private void _ChangeEpoch()
        {
            ++this._Epoch;
            this._SequenceNumber = -1;
        }

        private long _NextSequenceNumber() => ++this._SequenceNumber;

        private async Task _ProcessHandshakeAsync(DTLSRecord record)
        {
            if(record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            var data = record.Fragment;
            if (this._EncyptedServerEpoch == record.Epoch)
            {
                var count = 0;
                while ((this._Cipher == null) && (count < 500))
                {
                    await Task.Delay(10).ConfigureAwait(false);
                    count++;
                }

                if (this._Cipher == null)
                {
                    throw new Exception("Need Cipher for Encrypted Session");
                }
                
                var sequenceNumber = ((long)record.Epoch << 48) + record.SequenceNumber;
                data = this._Cipher.DecodeCiphertext(sequenceNumber, (byte)TRecordType.Handshake, record.Fragment, 0, record.Fragment.Length);
            }
            
            using (var tempStream = new MemoryStream(data))
            {
                var handshakeRec = HandshakeRecord.Deserialise(tempStream);
                if (handshakeRec.Length > (handshakeRec.FragmentLength + handshakeRec.FragmentOffset))
                {
                    this._IsFragment = true;
                    this._FragmentedRecordList.Add(data);
                    return;
                }
                else if (this._IsFragment)
                {
                    this._FragmentedRecordList.Add(data);
                    data = new byte[0];
                    foreach (var rec in this._FragmentedRecordList)
                    {
                        data = data.Concat(rec.Skip(HandshakeRecord.RECORD_OVERHEAD)).ToArray();
                    }

                    var tempHandshakeRec = new HandshakeRecord()
                    {
                        Length = handshakeRec.Length,
                        MessageSeq = handshakeRec.MessageSeq,
                        MessageType = handshakeRec.MessageType,
                        FragmentLength = handshakeRec.Length,
                        FragmentOffset = 0
                    };

                    var tempHandshakeBytes = new byte[HandshakeRecord.RECORD_OVERHEAD];
                    using (var updateStream = new MemoryStream(tempHandshakeBytes))
                    {
                        tempHandshakeRec.Serialise(updateStream);
                    }

                    data = tempHandshakeBytes.Concat(data).ToArray();
                }
            }

            using (var stream = new MemoryStream(data))
            {
                var handshakeRecord = HandshakeRecord.Deserialise(stream);
                switch (handshakeRecord.MessageType)
                {
                    case THandshakeType.HelloRequest:
                        {
                            break;
                        }
                    case THandshakeType.ClientHello:
                        {
                            break;
                        }
                    case THandshakeType.ServerHello:
                        {
                            var serverHello = ServerHello.Deserialise(stream);
                            this._HandshakeInfo.UpdateHandshakeHash(data);
                            this._ServerEpoch = record.Epoch;
                            this._HandshakeInfo.CipherSuite = (TCipherSuite)serverHello.CipherSuite;
                            this._HandshakeInfo.ServerRandom = serverHello.Random;
                            this._Version = serverHello.ServerVersion <= this._Version ? serverHello.ServerVersion : _SupportedVersion;
                            break;
                        }
                    case THandshakeType.HelloVerifyRequest:
                        {
                            var helloVerifyRequest = HelloVerifyRequest.Deserialise(stream);
                            this._Version = helloVerifyRequest.ServerVersion;
                            await this._SendHelloAsync(helloVerifyRequest.Cookie).ConfigureAwait(false);
                            break;
                        }
                    case THandshakeType.Certificate:
                        {
                            var cert = Certificate.Deserialise(stream, TCertificateType.X509);
                            this._HandshakeInfo.UpdateHandshakeHash(data);
                            this.ServerCertificate = cert.Cert;
                            break;
                        }
                    case THandshakeType.ServerKeyExchange:
                        {
                            this._HandshakeInfo.UpdateHandshakeHash(data);
                            var keyExchangeAlgorithm = CipherSuites.GetKeyExchangeAlgorithm(this._HandshakeInfo.CipherSuite);
                            byte[] preMasterSecret = null;
                            IKeyExchange keyExchange = null;
                            if (keyExchangeAlgorithm == TKeyExchangeAlgorithm.ECDHE_ECDSA)
                            {
                                var serverKeyExchange = ECDHEServerKeyExchange.Deserialise(stream, this._Version);
                                var keyExchangeECDHE = new ECDHEKeyExchange
                                {
                                    CipherSuite = this._HandshakeInfo.CipherSuite,
                                    Curve = serverKeyExchange.EllipticCurve,
                                    KeyExchangeAlgorithm = keyExchangeAlgorithm,
                                    ClientRandom = this._HandshakeInfo.ClientRandom,
                                    ServerRandom = this._HandshakeInfo.ServerRandom
                                };
                                keyExchangeECDHE.GenerateEphemeralKey();
                                var clientKeyExchange = new ECDHEClientKeyExchange(keyExchangeECDHE.PublicKey);
                                this._ClientKeyExchange = clientKeyExchange;
                                preMasterSecret = keyExchangeECDHE.GetPreMasterSecret(serverKeyExchange.PublicKeyBytes);
                                keyExchange = keyExchangeECDHE;
                            }
                            else if (keyExchangeAlgorithm == TKeyExchangeAlgorithm.ECDHE_PSK)
                            {
                                var serverKeyExchange = ECDHEPSKServerKeyExchange.Deserialise(stream);
                                var keyExchangeECDHE = new ECDHEKeyExchange
                                {
                                    CipherSuite = this._HandshakeInfo.CipherSuite,
                                    Curve = serverKeyExchange.EllipticCurve,
                                    KeyExchangeAlgorithm = keyExchangeAlgorithm,
                                    ClientRandom = this._HandshakeInfo.ClientRandom,
                                    ServerRandom = this._HandshakeInfo.ServerRandom
                                };
                                keyExchangeECDHE.GenerateEphemeralKey();
                                var clientKeyExchange = new ECDHEPSKClientKeyExchange(keyExchangeECDHE.PublicKey);
                                if (serverKeyExchange.PSKIdentityHint != null)
                                {
                                    var key = this.PSKIdentities.GetKey(serverKeyExchange.PSKIdentityHint);
                                    if (key != null)
                                    {
                                        this._PSKIdentity = new PSKIdentity() { Identity = serverKeyExchange.PSKIdentityHint, Key = key };
                                    }
                                }
                                if (this._PSKIdentity == null)
                                {
                                    this._PSKIdentity = this.PSKIdentities.GetRandom();
                                }

                                clientKeyExchange.PSKIdentity = this._PSKIdentity.Identity;
                                this._ClientKeyExchange = clientKeyExchange;
                                var otherSecret = keyExchangeECDHE.GetPreMasterSecret(serverKeyExchange.PublicKeyBytes);
                                preMasterSecret = TLSUtils.GetPSKPreMasterSecret(otherSecret, this._PSKIdentity.Key);
                                keyExchange = keyExchangeECDHE;
                            }
                            else if (keyExchangeAlgorithm == TKeyExchangeAlgorithm.PSK)
                            {
                                var serverKeyExchange = PSKServerKeyExchange.Deserialise(stream);
                                var clientKeyExchange = new PSKClientKeyExchange();
                                if (serverKeyExchange.PSKIdentityHint != null)
                                {
                                    var key = this.PSKIdentities.GetKey(serverKeyExchange.PSKIdentityHint);
                                    if (key != null)
                                    {
                                        this._PSKIdentity = new PSKIdentity() { Identity = serverKeyExchange.PSKIdentityHint, Key = key };
                                    }
                                }
                                if (this._PSKIdentity == null)
                                {
                                    this._PSKIdentity = this.PSKIdentities.GetRandom();
                                }

                                var otherSecret = new byte[this._PSKIdentity.Key.Length];
                                clientKeyExchange.PSKIdentity = this._PSKIdentity.Identity;
                                this._ClientKeyExchange = clientKeyExchange;
                                preMasterSecret = TLSUtils.GetPSKPreMasterSecret(otherSecret, this._PSKIdentity.Key);
                            }
                            this._Cipher = TLSUtils.AssignCipher(preMasterSecret, true, this._Version, this._HandshakeInfo);
                            break;
                        }
                    case THandshakeType.CertificateRequest:
                        {
                            this._HandshakeInfo.UpdateHandshakeHash(data);
                            this._SendCertificate = true;
                            break;
                        }
                    case THandshakeType.ServerHelloDone:
                        {
                            this._HandshakeInfo.UpdateHandshakeHash(data);
                            var keyExchangeAlgorithm = CipherSuites.GetKeyExchangeAlgorithm(this._HandshakeInfo.CipherSuite);
                            if (this._Cipher == null)
                            {
                                if (keyExchangeAlgorithm == TKeyExchangeAlgorithm.PSK)
                                {
                                    var clientKeyExchange = new PSKClientKeyExchange();
                                    this._PSKIdentity = this.PSKIdentities.GetRandom();
                                    var otherSecret = new byte[this._PSKIdentity.Key.Length];
                                    clientKeyExchange.PSKIdentity = this._PSKIdentity.Identity;
                                    this._ClientKeyExchange = clientKeyExchange;
                                    var preMasterSecret = TLSUtils.GetPSKPreMasterSecret(otherSecret, this._PSKIdentity.Key);
                                    this._Cipher = TLSUtils.AssignCipher(preMasterSecret, true, this._Version, this._HandshakeInfo);
                                }
                                else if (keyExchangeAlgorithm == TKeyExchangeAlgorithm.RSA)
                                {
                                    var clientKeyExchange = new RSAClientKeyExchange();
                                    this._ClientKeyExchange = clientKeyExchange;
                                    var PreMasterSecret = TLSUtils.GetRsaPreMasterSecret(this._Version);
                                    clientKeyExchange.PremasterSecret = TLSUtils.GetEncryptedRsaPreMasterSecret(this.ServerCertificate, PreMasterSecret);
                                    this._Cipher = TLSUtils.AssignCipher(PreMasterSecret, true, this._Version, this._HandshakeInfo);
                                }
                                else
                                {
                                    throw new NotImplementedException($"Key Exchange Algorithm {keyExchangeAlgorithm} Not Implemented");
                                }
                            }

                            if (this._SendCertificate)
                            {
                                await this._SendHandshakeMessageAsync(this._Certificate, false).ConfigureAwait(false);
                            }

                            await this._SendHandshakeMessageAsync(this._ClientKeyExchange, false).ConfigureAwait(false);

                            if (this._SendCertificate)
                            {
                                var signatureHashAlgorithm = new SignatureHashAlgorithm() { Signature = TSignatureAlgorithm.ECDSA, Hash = THashAlgorithm.SHA256 };
                                if (keyExchangeAlgorithm == TKeyExchangeAlgorithm.RSA)
                                {
                                    signatureHashAlgorithm = new SignatureHashAlgorithm() { Signature = TSignatureAlgorithm.RSA, Hash = THashAlgorithm.SHA1 };
                                }

                                var certVerify = new CertificateVerify
                                {
                                    SignatureHashAlgorithm = signatureHashAlgorithm,
                                    Signature = TLSUtils.Sign(this._PrivateKey, this._PrivateKeyRsa, true, this._Version, this._HandshakeInfo, signatureHashAlgorithm, this._HandshakeInfo.GetHash(this._Version))
                                };

                                await this._SendHandshakeMessageAsync(certVerify, false).ConfigureAwait(false);
                            }

                            await this._SendChangeCipherSpecAsync().ConfigureAwait(false);
                            var handshakeHash = this._HandshakeInfo.GetHash(this._Version);
                            var finished = new Finished
                            {
                                VerifyData = TLSUtils.GetVerifyData(this._Version, this._HandshakeInfo, true, true, handshakeHash)
                            };

                            await this._SendHandshakeMessageAsync(finished, true).ConfigureAwait(false);
                            break;
                        }
                    case THandshakeType.NewSessionTicket:
                        {
                            this._HandshakeInfo.UpdateHandshakeHash(data);
                            break;
                        }
                    case THandshakeType.CertificateVerify:
                        {
                            break;
                        }
                    case THandshakeType.ClientKeyExchange:
                        {
                            break;
                        }
                    case THandshakeType.Finished:
                        {
                            var serverFinished = Finished.Deserialise(stream);
                            var handshakeHash = this._HandshakeInfo.GetHash(this._Version);
                            var calculatedVerifyData = TLSUtils.GetVerifyData(this._Version, this._HandshakeInfo, true, false, handshakeHash);
                            if (serverFinished.VerifyData.SequenceEqual(calculatedVerifyData))
                            {
                                this._ConnectionComplete = true;
                            }
                            break;
                        }
                    default:
                        {
                            break;
                        }
                }
            }

            this._IsFragment = false;
            this._FragmentedRecordList.RemoveAll(x => true);
        }

        private async Task _ProcessRecordAsync(DTLSRecord record)
        {
            try
            {
                if (record == null)
                {
                    throw new ArgumentNullException(nameof(record));
                }

                switch (record.RecordType)
                {
                    case TRecordType.ChangeCipherSpec:
                        {
                            this._ReceivedData = new byte[0];
                            if (this._ServerEpoch.HasValue)
                            {
                                this._ServerEpoch++;
                                this._ServerSequenceNumber = 0;
                                this._EncyptedServerEpoch = this._ServerEpoch;
                            }
                            break;
                        }
                    case TRecordType.Alert:
                        {
                            this._ReceivedData = new byte[0];
                            AlertRecord alertRecord;
                            try
                            {
                                if ((this._Cipher == null) || (!this._EncyptedServerEpoch.HasValue))
                                {
                                    alertRecord = AlertRecord.Deserialise(record.Fragment);
                                }
                                else
                                {
                                    var sequenceNumber = ((long)record.Epoch << 48) + record.SequenceNumber;
                                    var data = this._Cipher.DecodeCiphertext(sequenceNumber, (byte)TRecordType.Alert, record.Fragment, 0, record.Fragment.Length);
                                    alertRecord = AlertRecord.Deserialise(data);
                                }
                            }
                            catch
                            {
                                alertRecord = new AlertRecord
                                {
                                    AlertLevel = TAlertLevel.Fatal
                                };
                            }
                            if (alertRecord.AlertLevel == TAlertLevel.Fatal)
                            {
                                this._ConnectionComplete = true;
                            }
                            else if ((alertRecord.AlertLevel == TAlertLevel.Warning) || (alertRecord.AlertDescription == TAlertDescription.CloseNotify))
                            {
                                if (alertRecord.AlertDescription == TAlertDescription.CloseNotify)
                                {
                                    await this._SendAlertAsync(TAlertLevel.Warning, TAlertDescription.CloseNotify).ConfigureAwait(false);
                                    this._ConnectionComplete = true;
                                }
                            }
                            break;
                        }
                    case TRecordType.Handshake:
                        {
                            this._ReceivedData = new byte[0];
                            await this._ProcessHandshakeAsync(record).ConfigureAwait(false);
                            this._ServerSequenceNumber = record.SequenceNumber + 1;
                            break;
                        }
                    case TRecordType.ApplicationData:
                        {
                            if (this._Cipher != null)
                            {
                                var sequenceNumber = ((long)record.Epoch << 48) + record.SequenceNumber;
                                var data = this._Cipher.DecodeCiphertext(sequenceNumber, (byte)TRecordType.ApplicationData, record.Fragment, 0, record.Fragment.Length);
                                this._DataReceivedFunction?.Invoke(record.RemoteEndPoint, data);
                                this._ReceivedData = data;
                            }
                            this._ServerSequenceNumber = record.SequenceNumber + 1;
                            break;
                        }
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private async Task _ProcessRecordsAsync()
        {
            while (!this._Terminate)
            {
                var record = this._Records.PeekRecord();
                while (record != null)
                {
                    if (this._ServerEpoch.HasValue && (this._ServerSequenceNumber != record.SequenceNumber || this._ServerEpoch != record.Epoch))
                    {
                       record = null;
                    }
                    else
                    {
                        this._Records.RemoveRecord();
                        await this._ProcessRecordAsync(record).ConfigureAwait(false);
                        record = this._Records.PeekRecord();
                    }
                }
                
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        private void _ReceiveCallback(byte[] recvData, EndPoint ip)
        {
            if(recvData == null)
            {
                throw new ArgumentNullException(nameof(recvData));
            }

            if(ip == null)
            {
                throw new ArgumentNullException(nameof(ip));
            }

            if(!recvData.Any())
            {
                //nothing received? return?
                return;
            }

            if(recvData.Length < 13)
            {
                this._RecvDataBuffer = this._RecvDataBuffer.Concat(recvData).ToArray();
                return;
            }

            var length = BitConverter.ToUInt16(recvData.Skip(11).Take(2).Reverse().ToArray(), 0);
            if (recvData.Length < length)
            {
                this._RecvDataBuffer = this._RecvDataBuffer.Concat(recvData).ToArray();
                return;
            }

            var fullData = this._RecvDataBuffer.Concat(recvData).ToArray();
            this._RecvDataBuffer = new byte[0];

            using (var stream = new MemoryStream(fullData))
            {
                while (stream.Position < stream.Length)
                {
                    var record = DTLSRecord.Deserialise(stream);
                    record.RemoteEndPoint = ip;
                    this._Records.Add(record);
                }
            }
        }

        private async Task<Socket> _SetupSocketAsync()
        {
            var addressFamily = this.LocalEndPoint.AddressFamily;
            var soc = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
            if (addressFamily == AddressFamily.InterNetworkV6)
            {
                soc.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
            }
#if NET452 || NET47
            if (Environment.OSVersion.Platform != PlatformID.Unix)
#else
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
#endif
            {
                // do not throw SocketError.ConnectionReset by ignoring ICMP Port Unreachable
                const int SIO_UDP_CONNRESET = -1744830452;
                soc.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }

            await soc.ConnectAsync(this._ServerEndPoint).ConfigureAwait(false);
            return soc;
        }

        public async Task SendAsync(byte[] data) => 
            await this.SendAsync(data, TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        public async Task SendAsync(byte[] data, TimeSpan timeout)
        {
            if(data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if(this._Socket == null)
            {
                throw new Exception("Socket Cannot be Null");
            }

            if(this._Cipher == null)
            {
                throw new Exception("Cipher Cannot be Null");
            }

            var record = new DTLSRecord
            {
                RecordType = TRecordType.ApplicationData,
                Epoch = _Epoch,
                SequenceNumber = this._NextSequenceNumber(),
                Version = this._Version
            };

            var sequenceNumber = ((long)record.Epoch << 48) + record.SequenceNumber;
            record.Fragment = this._Cipher.EncodePlaintext(sequenceNumber, (byte)TRecordType.ApplicationData, data, 0, data.Length);

            var recordSize = DTLSRecord.RECORD_OVERHEAD + record.Fragment.Length;
            var recordBytes = new byte[recordSize];
            using (var stream = new MemoryStream(recordBytes))
            {
                record.Serialise(stream);
            }

            await this._Socket.SendAsync(recordBytes, timeout).ConfigureAwait(false);
        }

        public async Task<byte[]> SendAndGetResponseAsync(byte[] data, TimeSpan timeout)
        {
            await this.SendAsync(data, timeout).ConfigureAwait(false);
            return await this.ReceiveDataAsync(timeout).ConfigureAwait(false);
        }

        public async Task<byte[]> SendAndGetResonseAsync(byte[] data) => 
            await this.SendAndGetResponseAsync(data, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        public async Task<byte[]> ReceiveDataAsync() =>
            await this.ReceiveDataAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        public async Task<byte[]> ReceiveDataAsync(TimeSpan timeout)
        {
            var startTime = DateTime.Now;
            while ((this._ReceivedData == null || !this._ReceivedData.Any()))
            {
                if((DateTime.Now - startTime) >= timeout)
                {
                    throw new TimeoutException();
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            return this._ReceivedData;
        }

        private async Task _SendAlertAsync(TAlertLevel alertLevel, TAlertDescription alertDescription) =>
           await this._SendAlertAsync(alertLevel, alertDescription, TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        private async Task _SendAlertAsync(TAlertLevel alertLevel, TAlertDescription alertDescription, TimeSpan timeout)
        {
            if(this._Socket == null)
            {
                throw new Exception("Soket Cannot be Null");
            }

            var record = new DTLSRecord
            {
                RecordType = TRecordType.Alert,
                Epoch = _Epoch,
                SequenceNumber = this._NextSequenceNumber(),
                Version = this._Version
            };

            var sequenceNumber = ((long)record.Epoch << 48) + record.SequenceNumber;

            var data = new byte[2];
            data[0] = (byte)alertLevel;
            data[1] = (byte)alertDescription;
            record.Fragment = this._Cipher == null ? data : this._Cipher.EncodePlaintext(sequenceNumber, (byte)TRecordType.ApplicationData, data, 0, data.Length);
            var recordSize = DTLSRecord.RECORD_OVERHEAD + record.Fragment.Length;
            var recordBytes = new byte[recordSize];
            using (var stream = new MemoryStream(recordBytes))
            {
                record.Serialise(stream);
            }

            await this._Socket.SendAsync(recordBytes, timeout).ConfigureAwait(false);
        }

        private async Task _SendChangeCipherSpecAsync() =>
            await this._SendChangeCipherSpecAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        private async Task _SendChangeCipherSpecAsync(TimeSpan timeout)
        {
            if(this._Socket == null)
            {
                throw new Exception("Socket Cannot be Null");
            }

            var bytes = this._GetChangeCipherSpec();
            await this._Socket.SendAsync(bytes, timeout).ConfigureAwait(false);
            this._ChangeEpoch();
        }

        private byte[] _GetChangeCipherSpec()
        {
            var size = 1;
            var responseSize = DTLSRecord.RECORD_OVERHEAD + size;
            var response = new byte[responseSize];
            var record = new DTLSRecord
            {
                RecordType = TRecordType.ChangeCipherSpec,
                Epoch = _Epoch,
                SequenceNumber = this._NextSequenceNumber(),
                Fragment = new byte[size],
                Version = this._Version
            };

            record.Fragment[0] = 1;
            using (var stream = new MemoryStream(response))
            {
                record.Serialise(stream);
            }
            return response;
        }

        private IEnumerable<byte[]> _GetBytes(IHandshakeMessage handshakeMessage, bool encrypt)
        {
            if(handshakeMessage == null)
            {
                throw new ArgumentNullException(nameof(handshakeMessage));
            }

            var size = handshakeMessage.CalculateSize(this._Version);
            var maxPayloadSize = _MaxPacketSize - DTLSRecord.RECORD_OVERHEAD + HandshakeRecord.RECORD_OVERHEAD;

            if (size > maxPayloadSize)
            {
                var wholeMessage = new List<byte[]>();

                var record = new DTLSRecord
                {
                    RecordType = TRecordType.Handshake,
                    Epoch = _Epoch,
                    Version = this._Version
                };

                var handshakeRecord = new HandshakeRecord
                {
                    MessageType = handshakeMessage.MessageType,
                    MessageSeq = _MessageSequence
                };

                if (!(handshakeMessage.MessageType == THandshakeType.HelloVerifyRequest
                   || (handshakeMessage.MessageType == THandshakeType.ClientHello && (handshakeMessage as ClientHello).Cookie == null)))
                {
                    record.Fragment = new byte[HandshakeRecord.RECORD_OVERHEAD + size];
                    handshakeRecord.Length = (uint)size;
                    handshakeRecord.FragmentLength = (uint)size;
                    handshakeRecord.FragmentOffset = 0u;
                    using (var stream = new MemoryStream(record.Fragment))
                    {
                        handshakeRecord.Serialise(stream);
                        handshakeMessage.Serialise(stream, this._Version);
                    }

                    this._HandshakeInfo.UpdateHandshakeHash(record.Fragment);
                }

                var dataMessage = new byte[size];
                using (var stream = new MemoryStream(dataMessage))
                {
                    handshakeMessage.Serialise(stream, this._Version);
                }

                var dataMessageFragments = dataMessage.ChunkBySize(maxPayloadSize);
                handshakeRecord.FragmentOffset = 0U;
                dataMessageFragments.ForEach(x =>
                {
                    handshakeRecord.Length = (uint)size;
                    handshakeRecord.FragmentLength = (uint)x.Count();
                    record.SequenceNumber = this._NextSequenceNumber();

                    var baseMessage = new byte[HandshakeRecord.RECORD_OVERHEAD];
                    using (var stream = new MemoryStream(baseMessage))
                    {
                        handshakeRecord.Serialise(stream);
                    }

                    record.Fragment = baseMessage.Concat(x).ToArray();

                    var responseSize = DTLSRecord.RECORD_OVERHEAD + HandshakeRecord.RECORD_OVERHEAD + x.Count();
                    if ((this._Cipher != null) && encrypt)
                    {
                        var sequenceNumber = ((long)record.Epoch << 48) + record.SequenceNumber;
                        record.Fragment = this._Cipher.EncodePlaintext(sequenceNumber, (byte)TRecordType.Handshake, record.Fragment, 0, record.Fragment.Length);
                        responseSize = DTLSRecord.RECORD_OVERHEAD + record.Fragment.Length;
                    }
                    var response = new byte[responseSize];
                    using (var stream = new MemoryStream(response))
                    {
                        record.Serialise(stream);
                    }

                    wholeMessage.Add(response);
                    handshakeRecord.FragmentOffset += (uint)x.Count();
                });

                this._MessageSequence++;
                return wholeMessage;
            }
            else
            {
                var record = new DTLSRecord
                {
                    RecordType = TRecordType.Handshake,
                    Epoch = _Epoch,
                    SequenceNumber = this._NextSequenceNumber(),
                    Fragment = new byte[HandshakeRecord.RECORD_OVERHEAD + size],
                    Version = this._Version
                };

                var handshakeRecord = new HandshakeRecord
                {
                    MessageType = handshakeMessage.MessageType,
                    MessageSeq = _MessageSequence
                };
                this._MessageSequence++;
                handshakeRecord.Length = (uint)size;
                handshakeRecord.FragmentLength = (uint)size;
                using (var stream = new MemoryStream(record.Fragment))
                {
                    handshakeRecord.Serialise(stream);
                    handshakeMessage.Serialise(stream, this._Version);
                }

                if (!(handshakeMessage.MessageType == THandshakeType.HelloVerifyRequest
                   || (handshakeMessage.MessageType == THandshakeType.ClientHello && (handshakeMessage as ClientHello).Cookie == null)))
                {
                    this._HandshakeInfo.UpdateHandshakeHash(record.Fragment);
                }

                var responseSize = DTLSRecord.RECORD_OVERHEAD + HandshakeRecord.RECORD_OVERHEAD + size;
                if ((this._Cipher != null) && encrypt)
                {
                    var sequenceNumber = ((long)record.Epoch << 48) + record.SequenceNumber;
                    record.Fragment = this._Cipher.EncodePlaintext(sequenceNumber, (byte)TRecordType.Handshake, record.Fragment, 0, record.Fragment.Length);
                    responseSize = DTLSRecord.RECORD_OVERHEAD + record.Fragment.Length;
                }

                var response = new byte[responseSize];
                using (var stream = new MemoryStream(response))
                {
                    record.Serialise(stream);
                }

                return new List<byte[]>() { response };
            }
        }

        private async Task _SendHelloAsync(byte[] cookie)
        {
            var clientHello = new ClientHello
            {
                ClientVersion = this._Version,
                Random = this._HandshakeInfo.ClientRandom,
                Cookie = cookie
            };

            var cipherSuites = new ushort[this.SupportedCipherSuites.Count];
            var index = 0;
            foreach (var item in this.SupportedCipherSuites)
            {
                cipherSuites[index] = (ushort)item;
                index++;
            }
            clientHello.CipherSuites = cipherSuites;
            clientHello.CompressionMethods = new byte[1];
            clientHello.CompressionMethods[0] = 0;

            clientHello.Extensions = new Extensions
            {
                new Extension() { ExtensionType = TExtensionType.SessionTicketTLS },
                new Extension() { ExtensionType = TExtensionType.EncryptThenMAC },
                new Extension() { ExtensionType = TExtensionType.ExtendedMasterSecret },
            };

            var ellipticCurvesExtension = new EllipticCurvesExtension();
            for (var curve = 0; curve < (int)TEllipticCurve.secp521r1; curve++)
            {
                if (EllipticCurveFactory.SupportedCurve((TEllipticCurve)curve))
                {
                    ellipticCurvesExtension.SupportedCurves.Add((TEllipticCurve)curve);

                }
            }
            clientHello.Extensions.Add(new Extension(ellipticCurvesExtension));
            var cllipticCurvePointFormatsExtension = new EllipticCurvePointFormatsExtension();
            cllipticCurvePointFormatsExtension.SupportedPointFormats.Add(TEllipticCurvePointFormat.Uncompressed);
            clientHello.Extensions.Add(new Extension(cllipticCurvePointFormatsExtension));
            var signatureAlgorithmsExtension = new SignatureAlgorithmsExtension();
            signatureAlgorithmsExtension.SupportedAlgorithms.Add(new SignatureHashAlgorithm() { Hash = THashAlgorithm.SHA1, Signature = TSignatureAlgorithm.RSA });
            clientHello.Extensions.Add(new Extension(signatureAlgorithmsExtension));
            await this._SendHandshakeMessageAsync(clientHello, false, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        private async Task _SendHandshakeMessageAsync(IHandshakeMessage handshakeMessage, bool encrypt) =>
            await this._SendHandshakeMessageAsync(handshakeMessage, encrypt, TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        private async Task _SendHandshakeMessageAsync(IHandshakeMessage handshakeMessage, bool encrypt, TimeSpan timeout)
        {
            if(handshakeMessage == null)
            {
                throw new ArgumentNullException(nameof(handshakeMessage));
            }

            if(this._Socket == null)
            {
                throw new Exception("Socket Cannot be Null");
            }

            var byteArrayList = this._GetBytes(handshakeMessage, encrypt);
            foreach (var byteArray in byteArrayList)
            {
                Console.WriteLine($"Sending {handshakeMessage.MessageType} {byteArray.Count()}");
                await this._Socket.SendAsync(byteArray, timeout).ConfigureAwait(false);
            }
        }

        public async Task ConnectToServerAsync(EndPoint serverEndPoint)
        {
            if (serverEndPoint == null)
            {
                throw new ArgumentNullException(nameof(serverEndPoint));
            }

            await this.ConnectToServerAsync(serverEndPoint, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        public async Task ConnectToServerAsync(EndPoint serverEndPoint, TimeSpan receiveTimeout, TimeSpan connectionTimeout)
        {
            this._ServerEndPoint = serverEndPoint ?? throw new ArgumentNullException(nameof(serverEndPoint));
            if (this.SupportedCipherSuites.Count == 0)
            {
                this.SupportedCipherSuites.Add(TCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CCM_8); //Test 1.2
                this.SupportedCipherSuites.Add(TCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256); //Tested 1.0 1.2
                this.SupportedCipherSuites.Add(TCipherSuite.TLS_PSK_WITH_AES_128_CCM_8); //Test 1.2
                this.SupportedCipherSuites.Add(TCipherSuite.TLS_PSK_WITH_AES_128_CBC_SHA256); //Tested 1.0 1.2
                this.SupportedCipherSuites.Add(TCipherSuite.TLS_ECDHE_PSK_WITH_AES_128_CBC_SHA256); //Tested 1.0 1.2
                this.SupportedCipherSuites.Add(TCipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA);
            }

            this._Socket = await this._SetupSocketAsync().ConfigureAwait(false);
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            this._ProcessRecordTask  = Task.Run(() => this._ProcessRecordsAsync().ConfigureAwait(false), this._Cts.Token); //fire and forget
            this._ReceiveTask = Task.Run(() => this._StartReceiveAsync(this._Socket, receiveTimeout).ConfigureAwait(false), this._Cts.Token); // fire and forget
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            await this._SendHelloAsync(null).ConfigureAwait(false);

            var startTime = DateTime.Now;
            while (!this._ConnectionComplete)
            {
                if((DateTime.Now - startTime) >= connectionTimeout)
                {
                    throw new TimeoutException("Could Not Connect To Server");
                }

                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        public void LoadX509Certificate(X509Chain chain)
        {
            if (chain == null)
            {
                throw new ArgumentNullException(nameof(chain));
            }

            var mainCert = chain.ChainElements[0].Certificate;

#if NETSTANDARD2_1 || NETSTANDARD2_0
            this._PrivateKeyRsa = ((RSACng)mainCert.PrivateKey).Key;
            this.PublicKey = ((RSACng)mainCert.PublicKey.Key).Key;
#else
            this._PrivateKeyRsa = (RSACryptoServiceProvider)mainCert.PrivateKey;
            this.PublicKey = (RSACryptoServiceProvider)mainCert.PublicKey.Key;
#endif

            var certChain = new List<byte[]>();
            foreach(var element in chain.ChainElements)
            {
                certChain.Add(element.Certificate.GetRawCertData());
            }

            this._Certificate = new Certificate
            {
                CertChain = certChain,
                CertificateType = TCertificateType.X509
            };
        }

        public void LoadCertificateFromPem(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
            {
                throw new ArgumentNullException(nameof(filename));
            }

            using (var stream = File.OpenRead(filename))
            {
                this.LoadCertificateFromPem(stream);
            }
        }

        public void LoadCertificateFromPem(Stream stream)
        {
            if(stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            var chain = new List<byte[]>();
            var reader = new PemReader(new StreamReader(stream));
            var pem = reader.ReadPemObject();

            while (pem != null)
            {
                if (pem.Type.EndsWith("CERTIFICATE"))
                {
                    chain.Add(pem.Content);
                }
                else if (pem.Type.EndsWith("PRIVATE KEY"))
                {
                    this._PrivateKey = Certificates.GetPrivateKeyFromPEM(pem);
                }
                pem = reader.ReadPemObject();
            }
            this._Certificate = new Certificate
            {
                CertChain = chain,
                CertificateType = TCertificateType.X509
            };
        }

        private async Task _StartReceiveAsync(Socket socket, TimeSpan timeout)
        {
            if(socket == null)
            {
                throw new ArgumentNullException(nameof(socket));
            }

            while (!this._Terminate)
            {
                var available = socket.Available;
                if (available > 0)
                {
                    var buffer = new byte[available];
                    var recvd = await socket.ReceiveAsync(buffer, timeout).ConfigureAwait(false);
                    if (recvd < available)
                    {
                        buffer = buffer.Take(recvd).ToArray();
                    }

                    this._ReceiveCallback(buffer, socket.RemoteEndPoint);
                }
                else
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
        }

        public void SetDataReceivedFunction(Action<EndPoint, byte[]> function) => this._DataReceivedFunction = function;

        public void SetVersion(Version version) => this._Version = version ?? throw new ArgumentNullException(nameof(version));

        private void _Dispose(bool disposing)
        {
            //prevent multiple calls to Dispose
            if (this._Disposed)
            {
                return;
            }

            if (disposing)
            {
                this._Terminate = true;

                if (this._Socket != null)
                {
                    this._SendAlertAsync(TAlertLevel.Fatal, TAlertDescription.CloseNotify).ConfigureAwait(false).GetAwaiter().GetResult();
                    this._Socket.Dispose();
                    this._Socket = null;
                }

                this._Cts.Cancel();
                this._ReceiveTask = null;
                this._ProcessRecordTask = null;
            }

            //Tell the GC not to call the finalizer later
            GC.SuppressFinalize(this);
            this._Disposed = true;
        }

        public void Dispose() => this._Dispose(true);

        ~Client()
        {
            this._Dispose(false);
        }
    }
}

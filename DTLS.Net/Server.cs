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

using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Utilities.IO.Pem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
#if !NET452 && !NET47
using System.Runtime.InteropServices;
#endif
using System.Threading;

namespace DTLS
{
    public class Server
	{
        public delegate void DataReceivedEventHandler(EndPoint endPoint, byte[] data);
        public event DataReceivedEventHandler DataReceived;

        public delegate byte[] ValidatePSKEventHandler(byte[] identity);
        public event ValidatePSKEventHandler ValidatePSK;

        private int _ReceiveBufferSize;
        private int _SendBufferSize;
        private Socket _Socket;
        private ServerHandshake _Handshake;
        private Certificate _Certificate;
		private Org.BouncyCastle.Crypto.AsymmetricKeyParameter _PrivateKey;
		private readonly Sessions _Sessions;

        public EndPoint LocalEndPoint { get; }

        //The maximum safe UDP payload is 508 bytes. Except on an IPv6-only route, where the maximum payload is 1,212 bytes.
        //https://stackoverflow.com/questions/1098897/what-is-the-largest-safe-udp-packet-size-on-the-internet#:~:text=The%20maximum%20safe%20UDP%20payload%20is%20508%20bytes.&text=Except%20on%20an%20IPv6%2Donly,bytes%20may%20be%20preferred%20instead.
        public int MaxPacketSize { get; set; } = 1212;

        public PSKIdentities PSKIdentities { get; set; }

        public bool RequireClientCertificate { get; set; }

        public List<TCipherSuite> SupportedCipherSuites { get; }

        public int ReceiveBufferSize
        {
            get => this._ReceiveBufferSize;
            set
            {
                this._ReceiveBufferSize = value;
                if (this._Socket != null)
                {
                    this._Socket.ReceiveBufferSize = value;
                }
            }
        }

        public int SendBufferSize
        {
            get => this._SendBufferSize;
            set
            {
                this._SendBufferSize = value;
                if (this._Socket != null)
                {
                    this._Socket.SendBufferSize = value;
                }
            }
        }

        public Server(EndPoint localEndPoint)
		{
            this.LocalEndPoint = localEndPoint ?? throw new ArgumentNullException(nameof(localEndPoint));
            if (this.LocalEndPoint.AddressFamily != AddressFamily.InterNetworkV6)
            {
                MaxPacketSize = 508;
            }

            this._Sessions = new Sessions();
            this.PSKIdentities = new PSKIdentities();
            this.SupportedCipherSuites = new List<TCipherSuite>();
		}

        public Server(EndPoint localEndPoint, List<TCipherSuite> supportedCipherSuites)
        {
            this.LocalEndPoint = localEndPoint ?? throw new ArgumentNullException(nameof(localEndPoint));
            this.SupportedCipherSuites = supportedCipherSuites ?? throw new ArgumentNullException(nameof(supportedCipherSuites));
            this._Sessions = new Sessions();
            this.PSKIdentities = new PSKIdentities();
        }

        private void _CheckSession(Session session, DTLSRecord record)
        {
            if(session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if(record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            if ((session.ClientEpoch == record.Epoch) && (session.ClientSequenceNumber == record.SequenceNumber))
            {
                ThreadPool.QueueUserWorkItem(this._ProcessRecord, record);
            }
            else if (session.ClientEpoch > record.Epoch)
            {
                ThreadPool.QueueUserWorkItem(this._ProcessRecord, record);
            }
            else if ((session.ClientEpoch == record.Epoch) && (session.ClientSequenceNumber > record.SequenceNumber))
            {
                ThreadPool.QueueUserWorkItem(this._ProcessRecord, record);
            }
            else
            {
                var canProcessNow = false;
                lock (session)
                {
                    if ((session.ClientSequenceNumber == record.SequenceNumber) && (session.ClientEpoch == record.Epoch))
                    {
                        canProcessNow = true;
                    }
                    else
                    {
                        session.Records.Add(record);
                    }
                }
                if (canProcessNow)
                {
                    this._CheckSession(session, record);
                }
            }
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

        public string GetClientPSKIdentity(EndPoint clientEndPoint)
        {
            if(clientEndPoint == null)
            {
                throw new ArgumentNullException(nameof(clientEndPoint));
            }
            
            var address = clientEndPoint.Serialize();
            var session = this._Sessions.GetSession(address);
            if (session != null)
            {
                return session.PSKIdentity;
            }

            return null;
        }

        public CertificateInfo GetClientCertificateInfo(EndPoint clientEndPoint)
        {
            if(clientEndPoint == null)
            {
                throw new ArgumentNullException(nameof(clientEndPoint));
            }
            
            var address = clientEndPoint.Serialize();
            var session = this._Sessions.GetSession(address);
            if (session != null)
            {
                return session.CertificateInfo;
            }

            return null;
        }

        private void _ProcessRecord(object state)
        {
            if (!(state is DTLSRecord record))
            {
                throw new ArgumentException("State Object Must be a DTLSRecord");
            }
            
            Session session = null;
            try
            {
                var address = record.RemoteEndPoint.Serialize();
                session = this._Sessions.GetSession(address);
                if (session == null)
                {
                    this._ProcessRecord(address, session, record);
                    session = this._Sessions.GetSession(address);
                    if (session != null)
                    {
                        lock (session)
                        {
                            if (record.RecordType != TRecordType.ChangeCipherSpec)
                            {
                                session.ClientSequenceNumber++;
                            }
                        }
                    }

                    return;
                }

                var processRecord = false;
                if ((session.ClientEpoch == record.Epoch) && (session.ClientSequenceNumber == record.SequenceNumber))
                {
                    processRecord = true;
                }
                else if (session.ClientEpoch > record.Epoch)
                {
                    processRecord = true;
                }
                else if ((session.ClientEpoch == record.Epoch) && (session.ClientSequenceNumber > record.SequenceNumber))
                {
                    processRecord = true;
                }

                if (!processRecord)
                {
                    return;
                }

                do
                {
                    this._ProcessRecord(address, session, record);
                    lock (session)
                    {
                        if (record.RecordType != TRecordType.ChangeCipherSpec)
                        {
                            session.ClientSequenceNumber++;
                        }
                    }

                    record = session.Records.PeekRecord();
                    if (record == null)
                    {
                        break;
                    }

                    if ((session.ClientSequenceNumber == record.SequenceNumber) && (session.ClientEpoch == record.Epoch))
                    {
                        session.Records.RemoveRecord();
                        continue;
                    }

                    break;

                } while (record != null);
            }
            catch (TlsFatalAlert ex)
            {
                this._SendAlert(session, TAlertLevel.Fatal, (TAlertDescription)ex.AlertDescription);
            }
            catch
            {
                this._SendAlert(session,  TAlertLevel.Fatal, TAlertDescription.InternalError);
            }
        }

        private void _ProcessRecord(SocketAddress address, Session session, DTLSRecord record)
        {
            try
            {
                if(address == null)
                {
                    throw new ArgumentNullException(nameof(address));
                }

                if(record == null)
                {
                    throw new ArgumentNullException(nameof(record));
                }

                switch (record.RecordType)
                {
                    case TRecordType.ChangeCipherSpec:
                        {
                            if (session != null)
                            {
                                session.ClientEpoch++;
                                session.ClientSequenceNumber = 0;
                                session.SetEncyptChange(record);
                            }
                            break;
                        }
                    case TRecordType.Alert:
                        {
                            if (session == null)
                            {
                                break;
                            }

                            AlertRecord alertRecord;
                            try
                            {
                                if (session.Cipher == null)
                                {
                                    alertRecord = AlertRecord.Deserialise(record.Fragment);
                                }
                                else
                                {
                                    var sequenceNumber = ((long)record.Epoch << 48) + record.SequenceNumber;
                                    var data = session.Cipher.DecodeCiphertext(sequenceNumber, (byte)TRecordType.Alert, record.Fragment, 0, record.Fragment.Length);
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
                                this._Sessions.Remove(session, address);
                            }
                            else if (alertRecord.AlertDescription == TAlertDescription.CloseNotify)
                            {
                                this._SendAlert(session, TAlertLevel.Warning, TAlertDescription.CloseNotify);
                                this._Sessions.Remove(session, address);
                            }
                            else if (alertRecord.AlertLevel == TAlertLevel.Warning)
                            {
                                this._Sessions.Remove(session, address);
                            }
                            break;
                        }
                    case TRecordType.Handshake:
                        {
                            this._Handshake.ProcessHandshake(record);
                            break;
                        }
                    case TRecordType.ApplicationData:
                        {
                            if (session == null)
                            {
                                break;
                            }

                            if (session.Cipher != null)
                            {
                                var sequenceNumber = ((long)record.Epoch << 48) + record.SequenceNumber;
                                var data = session.Cipher.DecodeCiphertext(sequenceNumber, (byte)TRecordType.ApplicationData, record.Fragment, 0, record.Fragment.Length);
                                DataReceived?.Invoke(record.RemoteEndPoint, data);
                            }
                            break;
                        }
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                this._SendAlert(session, TAlertLevel.Fatal, TAlertDescription.InternalError);
            }
        }

         private void _ReceiveCallback(object sender, SocketAsyncEventArgs e)
        {
            if(e == null)
            {
                throw new ArgumentNullException(nameof(e));
            }

            var count = e.BytesTransferred;
            if (count == 0)
            {
                //do nothing?
                return;
            }

            var data = new byte[count];
            Buffer.BlockCopy(e.Buffer, 0, data, 0, count);
            using (var stream = new MemoryStream(data))
            {
                while (stream.Position < stream.Length)
                {
                    var record = DTLSRecord.Deserialise(stream);
                    record.RemoteEndPoint = e.RemoteEndPoint;
                    var address = record.RemoteEndPoint.Serialize();
                    var session = this._Sessions.GetSession(address);
                    if (session == null)
                    {
                        ThreadPool.QueueUserWorkItem(this._ProcessRecord, record);
                    }
                    else
                    {
                        this._CheckSession(session, record);
                    }
                }

                if (sender is Socket socket)
                {
                    var remoteEndPoint = socket.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : (EndPoint)new IPEndPoint(IPAddress.IPv6Any, 0);
                    e.RemoteEndPoint = remoteEndPoint;
                    e.SetBuffer(0, 4096);
                    socket.ReceiveFromAsync(e);
                }
            }
        }
               
		private Socket _SetupSocket(AddressFamily addressFamily)
		{
			var result = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
            if (addressFamily == AddressFamily.InterNetworkV6)
            {
                result.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
            }

#if NET452 || NET47
            if (Environment.OSVersion.Platform != PlatformID.Unix)
#else
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
#endif
            {
                // Do not throw SocketError.ConnectionReset by ignoring ICMP Port Unreachable
                const int SIO_UDP_CONNRESET = -1744830452;
                result.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }

			return result;
		}

        public void Send(EndPoint remoteEndPoint, byte[] data)
        {
            if(remoteEndPoint == null)
            {
                throw new ArgumentNullException(nameof(remoteEndPoint));
            }

            if(data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            var address = remoteEndPoint.Serialize();
            var session = this._Sessions.GetSession(address);
            if (session == null)
            {
                return;
            }

            try
            {
                var record = new DTLSRecord
                {
                    RecordType = TRecordType.ApplicationData,
                    Epoch = session.Epoch,
                    SequenceNumber = session.NextSequenceNumber()
                };

                if (session.Version != null)
                {
                    record.Version = session.Version;
                }

                var sequenceNumber = ((long)record.Epoch << 48) + record.SequenceNumber;
                record.Fragment = session.Cipher.EncodePlaintext(sequenceNumber, (byte)TRecordType.ApplicationData, data, 0, data.Length);
                var responseSize = DTLSRecord.RECORD_OVERHEAD + record.Fragment.Length;
                var response = new byte[responseSize];
                using (var stream = new MemoryStream(response))
                {
                    record.Serialise(stream);
                }
                var parameters = new SocketAsyncEventArgs()
                {
                    RemoteEndPoint = session.RemoteEndPoint
                };
                parameters.SetBuffer(response, 0, responseSize);
                this._Socket.SendToAsync(parameters);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private void _SendAlert(Session session, TAlertLevel alertLevel, TAlertDescription alertDescription)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            var record = new DTLSRecord
            {
                RecordType = TRecordType.Alert,
                Epoch = session.Epoch,
                SequenceNumber = session.NextSequenceNumber()
            };

            if (session.Version != null)
            {
                record.Version = session.Version;
            }

            var sequenceNumber = ((long)record.Epoch << 48) + record.SequenceNumber;

            var data = new byte[2];
            data[0] = (byte)alertLevel;
            data[1] = (byte)alertDescription;
            record.Fragment = session.Cipher == null
                ? data
                : session.Cipher.EncodePlaintext(sequenceNumber, (byte)TRecordType.ApplicationData, data, 0, data.Length);

            var responseSize = DTLSRecord.RECORD_OVERHEAD + record.Fragment.Length;
            var response = new byte[responseSize];
            using (var stream = new MemoryStream(response))
            {
                record.Serialise(stream);
            }
            var parameters = new SocketAsyncEventArgs()
            {
                RemoteEndPoint = session.RemoteEndPoint
            };
            parameters.SetBuffer(response, 0, responseSize);
            this._Socket.SendToAsync(parameters);
        }

		public void Start()
		{
            if (this.SupportedCipherSuites.Count == 0)
            {
                this.SupportedCipherSuites.Add(TCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CCM_8); //Test 1.2
                this.SupportedCipherSuites.Add(TCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256); //Tested 1.0 1.2
                this.SupportedCipherSuites.Add(TCipherSuite.TLS_ECDHE_PSK_WITH_AES_128_CBC_SHA256); //Tested 1.0 1.2
                this.SupportedCipherSuites.Add(TCipherSuite.TLS_PSK_WITH_AES_128_CCM_8); //Test 1.2
                this.SupportedCipherSuites.Add(TCipherSuite.TLS_PSK_WITH_AES_128_CBC_SHA256); //Tested 1.0 1.2
            }

            this._Socket = this._SetupSocket(this.LocalEndPoint.AddressFamily);
            this._Handshake = new ServerHandshake(this._Socket, this.MaxPacketSize, this.PSKIdentities, this.SupportedCipherSuites, this.RequireClientCertificate, ValidatePSK)
            {
                Certificate = this._Certificate,
                PrivateKey = this._PrivateKey,
                Sessions = this._Sessions
            };

            this._Socket.Bind(this.LocalEndPoint);
            this._StartReceive(this._Socket);
		}

        private void _StartReceive(Socket socket)
        {
            if(socket == null)
            {
                throw new ArgumentNullException(nameof(socket));
            }

            var parameters = new SocketAsyncEventArgs
            {
                RemoteEndPoint = socket.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0)
            };
            parameters.Completed += new EventHandler<SocketAsyncEventArgs>(this._ReceiveCallback);
            parameters.SetBuffer(new byte[4096], 0, 4096);
            socket.ReceiveFromAsync(parameters);
        }

		public void Stop()
		{
            if (this._Socket == null)
            {
                return;
            }

            this._Socket.Dispose();
            this._Socket = null;
		}
	}
}
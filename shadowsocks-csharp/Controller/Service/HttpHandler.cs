﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Shadowsocks.Model;
using Shadowsocks.Util.Sockets;
using System.Text.RegularExpressions;

namespace Shadowsocks.Controller.Service
{
    class HttpHandlerHandlerFactory : ITCPHandlerFactory
    {
        private static readonly ByteSearch.SearchTarget HttpSearch =
            new ByteSearch.SearchTarget(Encoding.UTF8.GetBytes("HTTP"));

        public bool CanHandle(byte[] firstPacket, int length)
        {
            return HttpSearch.SearchIn(firstPacket, 0, length) != -1;
        }

        public TCPHandler NewHandler(ShadowsocksController controller, Configuration config, TCPRelay tcprelay, Socket socket)
        {
            return new HttpHandler(controller, config, tcprelay, socket);
        }
    }

    class HttpHandler : TCPHandler
    {
        private const string HTTP_CRLF = "\r\n";

        private const string HTTP_CONNECT_200 =
            "HTTP/1.1 200 Connection established" + HTTP_CRLF +
            "Proxy-Connection: close" + HTTP_CRLF +
            "Proxy-Agent: Shadowsocks" + HTTP_CRLF +
            "" + HTTP_CRLF; // End with an empty line

        private readonly WrappedSocket _localSocket;


        private byte[] _lastBytes;
        private int _lastBytesIndex;
        private int _lastBytesLength;


        public HttpHandler(ShadowsocksController controller, Configuration config, TCPRelay tcprelay, Socket socket) : base(controller, config, tcprelay, socket)
        {
            _localSocket = new WrappedSocket(socket);
        }

        public override void StartHandshake(byte[] firstPacket, int length)
        {
            new LineReader(firstPacket, _localSocket, firstPacket, 0, length, OnLineRead, OnException, OnFinish,
                    Encoding.UTF8, HTTP_CRLF, null);
        }


        #region Header Parse

        private void OnException(Exception ex, object state)
        {
            Logging.LogUsefulException(ex);
            Close();
        }

        private static readonly Regex HttpRequestHeaderRegex = new Regex(@"^(?<method>[A-Z]+?) (?<path>[^\s]+) (?<tail>HTTP/1\.\d)$");

        private int _requestLineCount = 0;
        private bool _isConnect = false;

        private string _targetHost;
        private int _targetPort;
        private readonly Queue<string> _headers = new Queue<string>();

        private bool ParseHost(string host)
        {
            var locs = host.Split(':');
            _targetHost = locs[0];
            if (locs.Length > 1)
            {
                if (!int.TryParse(locs[1], out _targetPort))
                {
                    return false;
                }
            }
            else
            {
                _targetPort = 80;
            }

            return true;
        }

        private bool OnLineRead(string line, object state)
        {
            if (Closed)
            {
                return true;
            }

            Logging.Debug(line);

            if (_requestLineCount == 0)
            {
                var m = HttpRequestHeaderRegex.Match(line);
                if (m.Success)
                {
                    var method = m.Groups["method"].Value;
                    var path = m.Groups["path"].Value;

                    if (method == "CONNECT")
                    {
                        _isConnect = true;

                        if (!ParseHost(path))
                        {
                            throw new Exception("Bad http header: " + line);
                        }
                    }
                    else
                    {
                        var targetUrl = new Uri(path);

                        if (!ParseHost(targetUrl.Authority))
                        {
                            throw new Exception("Bad http header: " + line);
                        }

                        var newRequestLine = $"{method} {targetUrl.PathAndQuery} {m.Groups["tail"].Value}";
                        _headers.Enqueue(newRequestLine);
                    }
                }
                else
                {
                    throw new FormatException("Not a vaild request line");
                }
            }
            else
            {
                // Handle Proxy-x Headers

                if (!line.StartsWith("Proxy-"))
                {
                    _headers.Enqueue(line);
                }
                else
                {
                    if (line.StartsWith("Proxy-Connection: "))
                    {
                        _headers.Enqueue(line.Substring(6));
                    }
                }

                if (line.IsNullOrEmpty())
                {
                    return true;
                }

                if (!_isConnect)
                {
                    if (line.StartsWith("Host: "))
                    {
                        if (!ParseHost(line.Substring(6).Trim()))
                        {
                            throw new Exception("Bad http header: " + line);
                        }
                    }
                }
            }

            _requestLineCount++;

            return false;
        }

        private void OnFinish(byte[] lastBytes, int index, int length, object state)
        {
            if (Closed)
            {
                return;
            }

            if (_targetHost == null)
            {
                Logging.Error("Unknown host");
                Close();
            }
            else
            {
                if (length > 0)
                {
                    _lastBytes = lastBytes;
                    _lastBytesIndex = index;
                    _lastBytesLength = length;
                }

                StartConnect(SocketUtil.GetEndPoint(_targetHost, _targetPort));
            }
        }

        #endregion

        protected override void OnServerConnected(AsyncSession session)
        {
            if (_isConnect)
            {
                // http connect response
                SendConnectResponse(session);
            }
            else
            {
                // send header
                SendHeader(session);
            }
        }


        #region CONNECT

        private void SendConnectResponse(AsyncSession session)
        {
            var len = Encoding.UTF8.GetBytes(HTTP_CONNECT_200, 0, HTTP_CONNECT_200.Length, RemoteRecvBuffer, 0);
            _localSocket.BeginSend(RemoteRecvBuffer, 0, len, SocketFlags.None, Http200SendCallback, session);
        }

        private void Http200SendCallback(IAsyncResult ar)
        {
            if (Closed)
            {
                return;
            }

            try
            {
                _localSocket.EndSend(ar);

                StartPipe((AsyncSession) ar.AsyncState);
            }
            catch (ArgumentException)
            {
            }
            catch (Exception e)
            {
                Logging.LogUsefulException(e);
                Close();
            }
        }

        #endregion

        #region Other http method except CONNECT

        private void SendHeader(AsyncSession session)
        {
            var h = _headers.Dequeue() + HTTP_CRLF;
            var len = Encoding.UTF8.GetBytes(h, 0, h.Length, ConnetionRecvBuffer, 0);
            BeginSendToServer(len, session, HeaderSendCallback);
        }

        private void HeaderSendCallback(IAsyncResult ar)
        {
            if (Closed)
            {
                return;
            }

            try
            {
                var session = EndSendToServer(ar);

                if (_headers.Count > 0)
                {
                    SendHeader(session);
                }
                else
                {
                    StartPipe(session);
                }
            }
            catch (Exception e)
            {
                Logging.LogUsefulException(e);
                Close();
            }
        }

        #endregion

    }
}

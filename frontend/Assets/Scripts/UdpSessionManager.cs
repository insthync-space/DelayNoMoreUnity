using Google.Protobuf;
using Google.Protobuf.Collections;
using shared;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class UdpSessionManager {
    private static UdpSessionManager _instance;

    public static UdpSessionManager Instance {
        get {
            if (null == _instance) _instance = new UdpSessionManager();
            return _instance;
        }
    }

    // See comments in WsSessionManager for thread-safety concerns of "senderBuffer" & "recvBuffer".
    public Queue<WsReq> senderBuffer;
    public Queue<WsReq> recvBuffer;
    private IPEndPoint[] peerUdpAddrList;
    private UdpClient udpSession;

    private UdpSessionManager() {
        senderBuffer = new Queue<WsReq>();
        recvBuffer = new Queue<WsReq>();
    }

    public async Task openUdpSession(int roomCapacity, int selfJoinIndex, string udpTunnelIpStr, int udpTunnelPort, CancellationToken wsSessionCancellationToken) {
        Debug.Log(String.Format("openUdpSession#1: roomCapacity={0}, udpTunnelIpStr={1}, udpTunnelPort={2}, thread id={3}.", roomCapacity, udpTunnelIpStr, udpTunnelPort, Thread.CurrentThread.ManagedThreadId));
        peerUdpAddrList = new IPEndPoint[roomCapacity + 1];
        IPAddress udpTunnelIp;
        if (!IPAddress.TryParse(udpTunnelIpStr, out udpTunnelIp)) {
            var msg = String.Format("Invalid udpTunnelIpStr {0}", udpTunnelIpStr);
            Debug.LogError(msg);
            throw new FormatException(msg);
        }

        peerUdpAddrList[shared.Battle.MAGIC_JOIN_INDEX_SRV_UDP_TUNNEL] = new IPEndPoint(udpTunnelIp, udpTunnelPort);

        senderBuffer.Clear();
        recvBuffer.Clear();

        try {
            udpSession = new UdpClient(port: 0);
            // [WARNING] UdpClient class in .NET Standard 2.1 doesn't support CancellationToken yet! See https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.udpclient?view=netstandard-2.1 for more information. The cancellationToken here is still used to keep in pace with the WebSocket session, i.e. is WebSocket Session is closed, then UdpSession should be closed too.
            Debug.Log(String.Format("openUdpSession#2: roomCapacity={0}, udpTunnelIp={1}, udpTunnelPort={2}, thread id={3}.", roomCapacity, udpTunnelIp, udpTunnelPort, Thread.CurrentThread.ManagedThreadId));
            await Task.WhenAll(Receive(udpSession, wsSessionCancellationToken), Send(udpSession, roomCapacity, selfJoinIndex, wsSessionCancellationToken));
            Debug.Log("Both UdpSession 'Receive' and 'Send' tasks are ended.");
        } catch (Exception ex) {
            Debug.LogError(String.Format("Error opening udpSession: {0}", ex));
        }
    }

    private async Task Send(UdpClient udpSession, int roomCapacity, int selfJoinIndex, CancellationToken wsSessionCancellationToken) {
        Debug.Log(String.Format("Starts udpSession 'Send' loop"));
        WsReq toSendObj;
        try {
            while (true) {
                wsSessionCancellationToken.ThrowIfCancellationRequested();
                while (senderBuffer.TryDequeue(out toSendObj)) {
                    wsSessionCancellationToken.ThrowIfCancellationRequested();
                    var toSendBuffer = toSendObj.ToByteArray();
                    int successPeerCnt = 0;
                    for (int i = 1; i <= roomCapacity; i++) {
                        if (i == selfJoinIndex) continue;
                        if (null == peerUdpAddrList[i]) continue;
                        Debug.Log(String.Format("udpSession sending {0} to {1}", toSendObj, peerUdpAddrList[i]));
                        await udpSession.SendAsync(toSendBuffer, toSendBuffer.Length, peerUdpAddrList[i]);
                        successPeerCnt++;
                    }
                    if (successPeerCnt + 1 < roomCapacity) {
                        Debug.Log(String.Format("udpSession sending {0} to UdpTunnel {1}", toSendObj, peerUdpAddrList[shared.Battle.MAGIC_JOIN_INDEX_SRV_UDP_TUNNEL]));
                        await udpSession.SendAsync(toSendBuffer, toSendBuffer.Length, peerUdpAddrList[shared.Battle.MAGIC_JOIN_INDEX_SRV_UDP_TUNNEL]);
                    }
                }
            }
        } catch (Exception ex) {
            Debug.LogWarning(String.Format("UdpSession is stopping for 'Send' upon exception; ex={0}", ex));
        } finally {
            Debug.Log(String.Format("Ends udpSession 'Send' loop"));
        }
    }

    private async Task Receive(UdpClient udpSession, CancellationToken wsSessionCancellationToken) {
        Debug.Log(String.Format("Starts udpSession 'Receive' loop"));
        try {
            while (true) {
                wsSessionCancellationToken.ThrowIfCancellationRequested();
                var recvResult = await udpSession.ReceiveAsync(); // by experiment, "udpSession.Close()" would unblock it
                WsReq req = WsReq.Parser.ParseFrom(recvResult.Buffer);
                if (shared.Battle.UPSYNC_MSG_ACT_HOLEPUNCH == req.Act) {
                    Debug.Log(String.Format("Received holepuncher: {0}", req));
                } else {
                    recvBuffer.Enqueue(req);
                }
            }
        } catch (Exception ex) {
            Debug.LogWarning(String.Format("UdpSession is stopping for 'Receive' upon exception; ex={0}", ex));
        } finally {
            Debug.Log(String.Format("Ends udpSession 'Receive' loop"));
        }
    }

    public void updatePeerAddr(int roomCapacity, RepeatedField<PeerUdpAddr> newPeerUdpAddrList) {
        // TODO: Make this method thread-safe for "this.peerUdpAddrList"
        for (int i = 0; i <= roomCapacity; i++) {
            if (0 < i && String.IsNullOrEmpty(newPeerUdpAddrList[i].Ip)) continue;
            IPAddress newEndpointIp;
            if (!IPAddress.TryParse(Battle.MAGIC_JOIN_INDEX_SRV_UDP_TUNNEL == i ? Env.Instance.getHostnameOnly() : newPeerUdpAddrList[i].Ip, out newEndpointIp)) {
                var msg = String.Format("Invalid newEndpointIpStr {0} for joinIndex={1}", newPeerUdpAddrList[i].Ip, i);
                Debug.LogError(msg);
                throw new FormatException(msg);
            }
            peerUdpAddrList[i] = new IPEndPoint(newEndpointIp, newPeerUdpAddrList[i].Port); ;
        }
    }

    public void closeUdpSession() {
        if (null != udpSession) {
            udpSession.Close();
        }
    }
}
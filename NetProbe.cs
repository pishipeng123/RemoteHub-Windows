using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteHub
{
    /// <summary>Result of a host reachability check.</summary>
    internal enum ProbeState
    {
        Unknown,    // not checked yet
        Checking,   // probe in flight
        Online,     // RDP port accepts connections
        Reachable,  // ping answers, but the RDP port does not
        Offline     // neither
    }

    /// <summary>
    /// Small, dependency-free network probes. Everything is synchronous with a hard
    /// timeout so callers can safely fan the work out over the thread pool.
    /// </summary>
    internal static class NetProbe
    {
        public static bool PortOpen(string host, int port, int timeoutMs)
        {
            if (string.IsNullOrEmpty(host)) return false;
            if (timeoutMs < 100) timeoutMs = 100;
            TcpClient cli = null;
            try
            {
                cli = new TcpClient();
                IAsyncResult ar = cli.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false)) return false;
                cli.EndConnect(ar);
                return cli.Connected;
            }
            catch { return false; }
            finally
            {
                if (cli != null) { try { cli.Close(); } catch { } }
            }
        }

        public static bool Pingable(string host, int timeoutMs)
        {
            if (string.IsNullOrEmpty(host)) return false;
            if (timeoutMs < 100) timeoutMs = 100;
            try
            {
                using (Ping p = new Ping())
                {
                    PingReply r = p.Send(host, timeoutMs);
                    return r != null && r.Status == IPStatus.Success;
                }
            }
            catch { return false; }
        }

        /// <summary>First checks the RDP port, then falls back to ICMP.</summary>
        public static ProbeState Check(string host, int port, int timeoutMs)
        {
            if (PortOpen(host, port, timeoutMs)) return ProbeState.Online;
            if (Pingable(host, timeoutMs)) return ProbeState.Reachable;
            return ProbeState.Offline;
        }

        public static ProbeState Check(ConnectionInfo c, int timeoutMs)
        {
            if (c == null || string.IsNullOrEmpty(c.Host)) return ProbeState.Unknown;
            return Check(c.Host, c.Port > 0 ? c.Port : 3389, timeoutMs);
        }

        // ---- IP range parsing -------------------------------------------

        private static uint ToUInt(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return BitConverter.ToUInt32(b, 0);
        }

        private static IPAddress FromUInt(uint v)
        {
            byte[] b = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return new IPAddress(b);
        }

        /// <summary>
        /// Accepts a single IP, "a.b.c.d-e.f.g.h", "a.b.c.d-200" and CIDR "a.b.c.0/24".
        /// Returns at most 1024 addresses to keep a mistyped range harmless.
        /// </summary>
        public static List<string> ExpandRange(string spec, out string error)
        {
            error = null;
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(spec)) { error = "请填写 IP 或 IP 段。"; return result; }
            spec = spec.Trim();

            int slash = spec.IndexOf('/');
            if (slash > 0)
            {
                IPAddress net;
                int bits;
                if (!IPAddress.TryParse(spec.Substring(0, slash), out net) ||
                    !int.TryParse(spec.Substring(slash + 1), out bits) || bits < 16 || bits > 30)
                {
                    error = "CIDR 格式应为 192.168.1.0/24（掩码 16–30）。";
                    return result;
                }
                uint mask = bits == 32 ? 0xFFFFFFFFu : (0xFFFFFFFFu << (32 - bits));
                uint start = ToUInt(net) & mask;
                uint end = start | ~mask;
                // Skip network and broadcast addresses.
                for (uint v = start + 1; v < end && result.Count < 1024; v++)
                    result.Add(FromUInt(v).ToString());
                return result;
            }

            int dash = spec.IndexOf('-');
            if (dash > 0)
            {
                IPAddress first;
                if (!IPAddress.TryParse(spec.Substring(0, dash), out first))
                {
                    error = "起始 IP 无效。";
                    return result;
                }
                string tail = spec.Substring(dash + 1).Trim();
                uint end;
                if (tail.Contains("."))
                {
                    IPAddress last;
                    if (!IPAddress.TryParse(tail, out last)) { error = "结束 IP 无效。"; return result; }
                    end = ToUInt(last);
                }
                else
                {
                    int lastOctet;
                    if (!int.TryParse(tail, out lastOctet) || lastOctet < 1 || lastOctet > 254)
                    {
                        error = "结束段应为 1–254 或完整 IP。";
                        return result;
                    }
                    uint s = ToUInt(first);
                    end = (s & 0xFFFFFF00u) | (uint)lastOctet;
                }
                uint a = ToUInt(first);
                if (end < a) { error = "结束地址不能小于起始地址。"; return result; }
                if (end - a > 1023) { error = "一次最多扫描 1024 个地址，请缩小范围。"; return result; }
                for (uint v = a; v <= end; v++) result.Add(FromUInt(v).ToString());
                return result;
            }

            IPAddress one;
            if (!IPAddress.TryParse(spec, out one)) { error = "IP 格式无效。"; return result; }
            result.Add(one.ToString());
            return result;
        }
    }
}

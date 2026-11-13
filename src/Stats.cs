using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AgentWatch
{
    class GpuStat { public int Index; public string Name; public double Util, MemUsed, MemTotal, Temp = -1; public bool Ok; }
    class DriveStat { public string Letter; public double Free, Total; public bool Ok; }

    // one immutable sample of the machine; the sampler swaps in a new one every ~2 s
    class SysSample
    {
        public DateTime At = DateTime.Now;
        public double Cpu = -1, RamUsed, RamTotal, NetUp, NetDown, DiskRead = -1, DiskWrite = -1, CostMs;
        public List<GpuStat> Gpus = new List<GpuStat>();
        public List<DriveStat> Drives = new List<DriveStat>();
        public string GpuNote;
    }

    // Cheap live machine stats for the header tiles.
    // CPU: GetSystemTimes. RAM: GlobalMemoryStatusEx. GPU: NVML (nvml.dll from the NVIDIA driver) - no nvidia-smi, no WMI.
    // Network: adapter byte counters. Disk: two reused PhysicalDisk performance counters. Free space: DriveInfo every 20 s.
    // Runs on one low-priority background thread and only while the window is visible.
    class SysStats
    {
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [StructLayout(LayoutKind.Sequential)]
        class MEMSTAT { public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMSTAT)); public uint dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMSTAT m);

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] static extern int nvmlInit();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")] static extern int nvmlDeviceGetCount(out uint n);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] static extern int nvmlDeviceGetHandleByIndex(uint i, out IntPtr d);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")] static extern int nvmlDeviceGetName(IntPtr d, byte[] name, uint len);
        [StructLayout(LayoutKind.Sequential)] struct NvUtil { public uint gpu, memory; }
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")] static extern int nvmlDeviceGetUtilizationRates(IntPtr d, out NvUtil u);
        [StructLayout(LayoutKind.Sequential)] struct NvMem { public ulong total, free, used; }
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")] static extern int nvmlDeviceGetMemoryInfo(IntPtr d, out NvMem m);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] static extern int nvmlDeviceGetTemperature(IntPtr d, int sensor, out uint t);

        public volatile SysSample Last = new SysSample();
        public volatile bool Active;
        public string[] DriveLetters = { "C", "D", "I" };
        ManualResetEvent wake = new ManualResetEvent(false);
        Thread th;

        long lIdle, lKern, lUser; bool haveCpu;
        long lRx, lTx; DateTime lNetAt; bool haveNet;
        PerformanceCounter dRead, dWrite; bool diskTried;
        bool nvOk, nvTried; List<IntPtr> nvDev = new List<IntPtr>(); List<string> nvName = new List<string>(); DateTime nvEnumAt = DateTime.MinValue; string nvNote;
        List<DriveStat> drives = new List<DriveStat>(); DateTime drivesAt = DateTime.MinValue;

        public void Start()
        {
            if (th != null) return;
            th = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "AgentWatch stats" };
            th.Start();
        }
        public void SetActive(bool on) { Active = on; if (on) wake.Set(); else wake.Reset(); }

        void Loop()
        {
            while (true)
            {
                try
                {
                    if (!Active) { wake.WaitOne(); haveNet = false; haveCpu = false; }
                    var sw = Stopwatch.StartNew();
                    var s = Sample();
                    s.CostMs = sw.Elapsed.TotalMilliseconds;
                    Last = s;
                }
                catch (Exception ex) { Logger.Error("stats: " + ex.Message); }
                Thread.Sleep(2000);
            }
        }

        SysSample Sample()
        {
            var s = new SysSample();
            // CPU
            long idle, kern, user;
            if (GetSystemTimes(out idle, out kern, out user))
            {
                if (haveCpu)
                {
                    long di = idle - lIdle, dt = (kern - lKern) + (user - lUser);
                    if (dt > 0) s.Cpu = Math.Max(0, Math.Min(100, (dt - di) * 100.0 / dt));
                }
                lIdle = idle; lKern = kern; lUser = user; haveCpu = true;
            }
            // RAM
            var m = new MEMSTAT();
            if (GlobalMemoryStatusEx(m)) { s.RamTotal = m.ullTotalPhys; s.RamUsed = m.ullTotalPhys - m.ullAvailPhys; }
            // network: sum of up, non-loopback, non-tunnel adapters (Hyper-V virtual adapters skipped so traffic is not counted twice)
            try
            {
                long rx = 0, tx = 0;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var ty = ni.NetworkInterfaceType;
                    if (ty == NetworkInterfaceType.Loopback || ty == NetworkInterfaceType.Tunnel) continue;
                    string d = ni.Description ?? "";
                    if (d.IndexOf("Hyper-V Virtual", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var st = ni.GetIPStatistics(); rx += st.BytesReceived; tx += st.BytesSent;
                }
                var now = DateTime.Now;
                if (haveNet) { double sec = (now - lNetAt).TotalSeconds; if (sec > 0.2) { s.NetDown = Math.Max(0, (rx - lRx) / sec); s.NetUp = Math.Max(0, (tx - lTx) / sec); } }
                lRx = rx; lTx = tx; lNetAt = now; haveNet = true;
            }
            catch { }
            // disk throughput (two counters, created once)
            if (!diskTried)
            {
                diskTried = true;
                try { dRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true); dWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true); dRead.NextValue(); dWrite.NextValue(); }
                catch (Exception ex) { dRead = null; dWrite = null; Logger.Error("stats: disk counters unavailable: " + ex.Message); }
            }
            if (dRead != null) { try { s.DiskRead = dRead.NextValue(); s.DiskWrite = dWrite.NextValue(); } catch { } }
            // free space every 20 s
            if ((DateTime.Now - drivesAt).TotalSeconds >= 20)
            {
                drivesAt = DateTime.Now; var nd = new List<DriveStat>();
                foreach (var L in DriveLetters)
                {
                    var ds = new DriveStat { Letter = L };
                    try { var di = new DriveInfo(L); if (di.IsReady) { ds.Free = di.TotalFreeSpace; ds.Total = di.TotalSize; ds.Ok = true; } } catch { }
                    nd.Add(ds);
                }
                drives = nd;
            }
            s.Drives = drives;
            // GPUs via NVML
            Gpu(s);
            return s;
        }

        void Gpu(SysSample s)
        {
            if (!nvTried)
            {
                nvTried = true;
                try { int r = nvmlInit(); nvOk = r == 0; if (!nvOk) nvNote = "NVIDIA driver did not answer (code " + r + ")"; }
                catch (DllNotFoundException) { nvNote = "No NVIDIA driver found"; }
                catch (EntryPointNotFoundException) { nvNote = "NVIDIA driver too old"; }
                catch (Exception ex) { nvNote = "GPU stats unavailable: " + ex.Message; }
            }
            if (!nvOk) { s.GpuNote = nvNote; return; }
            // re-list devices every 5 minutes (cards can be removed)
            if ((DateTime.Now - nvEnumAt).TotalMinutes >= 5)
            {
                nvEnumAt = DateTime.Now; nvDev.Clear(); nvName.Clear();
                uint n; if (nvmlDeviceGetCount(out n) == 0)
                    for (uint i = 0; i < n; i++)
                    {
                        IntPtr d; if (nvmlDeviceGetHandleByIndex(i, out d) != 0) continue;
                        var b = new byte[96]; string name = "GPU " + i;
                        if (nvmlDeviceGetName(d, b, (uint)b.Length) == 0) { int z = Array.IndexOf(b, (byte)0); name = Encoding.ASCII.GetString(b, 0, z < 0 ? b.Length : z); }
                        nvDev.Add(d); nvName.Add(name.Replace("NVIDIA ", "").Replace("GeForce ", ""));
                    }
            }
            if (nvDev.Count == 0) { s.GpuNote = "No NVIDIA GPU found"; return; }
            for (int i = 0; i < nvDev.Count; i++)
            {
                var g = new GpuStat { Index = i, Name = nvName[i] };
                NvUtil u; NvMem mm; uint t;
                if (nvmlDeviceGetUtilizationRates(nvDev[i], out u) == 0) { g.Util = u.gpu; g.Ok = true; }
                if (nvmlDeviceGetMemoryInfo(nvDev[i], out mm) == 0) { g.MemUsed = mm.used; g.MemTotal = mm.total; g.Ok = true; }
                if (nvmlDeviceGetTemperature(nvDev[i], 0, out t) == 0) g.Temp = t;
                if (!g.Ok) nvEnumAt = DateTime.MinValue; // lost card: re-list next time
                s.Gpus.Add(g);
            }
        }
    }
}

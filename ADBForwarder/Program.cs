﻿using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

using SharpAdbClient;
using ICSharpCode.SharpZipLib.Zip;

namespace ADBForwarder
{
    public class LogShellOutputReceiver : IShellOutputReceiver
    {
        public readonly ConcurrentQueue<string> LogShellOutputs = new ConcurrentQueue<string>();

        public bool ParsesErrors => false;

        public void AddOutput(string line)
        {
            LogShellOutputs.Enqueue(line);
        }

        public void Flush()
        {
            string outputBuffer;
            while (!LogShellOutputs.IsEmpty)
            {
                if (LogShellOutputs.TryDequeue(out outputBuffer))
                {
                    Console.WriteLine(outputBuffer);
                }
            }
        }
    }

    internal class Program
    {
        public const string VERSION = "0.2";
        
        private static readonly string[] deviceNames =
        {
            "monterey",        // Oculus Quest 1
            "hollywood",       // Oculus Quest 2
            "pacific",         // Oculus Go
            "vr_monterey",     // Edge case for linux, Quest 1
            "vr_hollywood",    // Edge case for linux, Oculus Quest 2
            "vr_pacific"       // Edge case for linux, Oculus Go
        };
        
        private static readonly AdbClient client = new AdbClient();
        private static readonly AdbServer server = new AdbServer();
        private static readonly IPEndPoint endPoint = new IPEndPoint(IPAddress.Loopback, AdbClient.AdbServerPort);
        private static readonly LogShellOutputReceiver outputReceiver = new LogShellOutputReceiver();

        private static void Main()
        {
            Console.ResetColor();
            var currentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (currentDirectory == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Path error!");
                return;
            }
            
            var adbPath = "adb/platform-tools/{0}";
            var downloadUri = "https://dl.google.com/android/repository/platform-tools-latest-{0}.zip";
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Console.WriteLine("Platform: Linux");
                
                adbPath = string.Format(adbPath, "adb");
                downloadUri = string.Format(downloadUri, "linux");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine("Platform: Windows");
                
                adbPath = string.Format(adbPath, "adb.exe");
                downloadUri = string.Format(downloadUri, "windows");
            }
            else
            {
                Console.WriteLine("Unsupported platform!");
                return;
            }

            var absoluteAdbPath = Path.Combine(currentDirectory, adbPath);
            if (!File.Exists(absoluteAdbPath))
            {
                Console.WriteLine("ADB not found, downloading in the background...");
                DownloadADB(downloadUri);
                
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    SetExecutable(absoluteAdbPath);
            }

            Console.WriteLine("Starting ADB Server...");
            server.StartServer(absoluteAdbPath, false);

            client.Connect(endPoint);
            
            var monitor = new DeviceMonitor(new AdbSocket(endPoint));
            monitor.DeviceConnected += Monitor_DeviceConnected;
            monitor.DeviceDisconnected += Monitor_DeviceDisconnected;
            monitor.Start();

            while (true)
            {
                // Main thread needs to stay alive, 100ms is acceptable idle time
                Thread.Sleep(100);
            }
        }

        private static void Monitor_DeviceConnected(object sender, DeviceDataEventArgs e)
        {
            Console.ForegroundColor = ConsoleColor.DarkGreen;
            Console.WriteLine($"Connected device: {e.Device.Serial}");
            Forward(e.Device);
        }

        private static void Monitor_DeviceDisconnected(object sender, DeviceDataEventArgs e)
        {
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine($"Disconnected device: {e.Device.Serial}");
        }
        
        private static void Forward(DeviceData device)
        {
            // DeviceConnected calls without product set yet
            Thread.Sleep(1000);

            foreach (var deviceData in client.GetDevices().Where(deviceData => device.Serial == deviceData.Serial))
            {
                if (!deviceNames.Contains(deviceData.Product))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Skipped forwarding device: {(string.IsNullOrEmpty(deviceData.Product) ? deviceData.Serial : deviceData.Product)}");
                    return;
                }

                client.CreateForward(deviceData, 9943, 9943);
                client.CreateForward(deviceData, 9944, 9944);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Successfully forwarded device: {deviceData.Serial} [{deviceData.Product}]");

                client.ExecuteRemoteCommand("am start -n alvr.client.quest/com.polygraphene.alvr.OvrActivity", deviceData, outputReceiver);

                return;
            }
        }
        
        private static void DownloadADB(string downloadUri)
        {
            using var web = new WebClient();
            web.DownloadFile(downloadUri, "adb.zip");
            Console.WriteLine("Download successful");
            
            var zip = new FastZip();
            zip.ExtractZip("adb.zip", "adb", null);
            Console.WriteLine("Extraction successful");
            
            File.Delete("adb.zip");
        }

        private static void SetExecutable(string fileName)
        {
            Console.WriteLine("Giving adb executable permissions");

            var args = $"chmod u+x {fileName}";
            var escapedArgs = args.Replace("\"", "\\\"");
        
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{escapedArgs}\""
                }
            };

            process.Start();
            process.WaitForExit();
        }
    }
}
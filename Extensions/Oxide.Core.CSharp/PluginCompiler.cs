﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using Mono.Unix.Native;
using ObjectStream;
using ObjectStream.Data;
using Oxide.Core;

namespace Oxide.Plugins
{
    public class PluginCompiler
    {
        public static bool AutoShutdown = true;
        public static string BinaryPath;
        public static string CompilerVersion;

        public static void CheckCompilerBinary()
        {
            BinaryPath = null;
            var filename = "basic.exe";
            var rootDirectory = Interface.Oxide.RootDirectory;
            var binaryPath = Path.Combine(rootDirectory, filename);
            if (File.Exists(binaryPath))
            {
                BinaryPath = binaryPath;
                return;
            }

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                case PlatformID.Win32S:
                case PlatformID.Win32Windows:
                    filename = "CSharpCompiler.exe";
                    binaryPath = Path.Combine(rootDirectory, filename);
                    if (!File.Exists(binaryPath))
                    {
                        try
                        {
                            UpdateCheck(filename); // TODO: Only check once on server startup
                        }
                        catch (Exception)
                        {
                            Interface.Oxide.LogError($"Cannot compile C# (.cs) plugins; unable to find {filename}");
                            return;
                        }
                    }
                    break;
                case PlatformID.Unix:
                case PlatformID.MacOSX:
                    filename = $"CSharpCompiler.{(IntPtr.Size != 8 ? "x86" : "x86_x64")}";
                    binaryPath = Path.Combine(rootDirectory, filename);
                    if (!File.Exists(binaryPath))
                    {
                        try
                        {
                            UpdateCheck(filename); // TODO: Only check once on server startup
                        }
                        catch (Exception ex)
                        {
                            Interface.Oxide.LogError($"Cannot compile .cs (C#) plugins; unable to find {filename}");
                            Interface.Oxide.LogWarning(ex.Message);
                            return;
                        }
                    }
                    try
                    {
                        if (Syscall.access(binaryPath, AccessModes.X_OK) == -1)
                        {
                            try
                            {
                                Syscall.chmod(binaryPath, FilePermissions.S_IRWXU);
                            }
                            catch (Exception ex)
                            {
                                Interface.Oxide.LogError($"Could not set {filename} as executable; please set manually");
                                Interface.Oxide.LogError(ex.Message);
                            }
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Interface.Oxide.LogError($"Cannot compile .cs (C#) plugins; {filename} is not executable");
                        Interface.Oxide.LogError(ex.Message);
                        return;
                    }
                    break;
            }
            BinaryPath = binaryPath;
        }

        private static void DependencyTrace()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) return;

            try
            {
                var startInfo = new ProcessStartInfo("LD_TRACE_LOADED_OBJECTS=1", BinaryPath)
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                var process = Process.Start(startInfo);
                process.OutputDataReceived += (s, e) => Interface.Oxide.LogError(e.Data);
                process.BeginOutputReadLine();
                process.Start();
                process.WaitForExit();
                //Thread.Sleep(5000);
            }
            catch { }
        }

        private static void UpdateCheck(string filename)
        {
            var filePath = Path.Combine(Interface.Oxide.RootDirectory, filename);

            HttpWebRequest request;
            try
            {
                request = (HttpWebRequest)WebRequest.Create($"https://dl.bintray.com/oxidemod/builds/{filename}");
            }
            catch (Exception ex)
            {
                Interface.Oxide.LogWarning("Main download location failed, using mirror");
                request = (HttpWebRequest)WebRequest.Create($"https://bintray.com/oxidemod/builds/download_file?file_path={filename}");
                Interface.Oxide.LogWarning(ex.Message);
            }

            var response = (HttpWebResponse)request.GetResponse();
            var statusCode = (int)response.StatusCode;
            if (statusCode != 200) Interface.Oxide.LogWarning($"Status code from download location was not okay; code {statusCode}");

            var etag = response.Headers[HttpResponseHeader.ETag];
            var remoteChecksum = etag.Substring(0, etag.LastIndexOf(':')).Trim('"').ToLower();
            var localChecksum = "0";

            if (File.Exists(filePath)) localChecksum = GetChecksum(filePath, Algorithms.MD5);
            if (remoteChecksum != localChecksum) DownloadCompiler(filename, response);
        }

        private static void DownloadCompiler(string filename, WebResponse response)
        {
            try
            {
                Interface.Oxide.LogInfo($"Downloading {filename} for .cs (C#) plugin compilation");

                var stream = response.GetResponseStream();
                var fs = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None);
                var bufferSize = 10000;
                var buffer = new byte[bufferSize];

                while (true)
                {
                    var result = stream.Read(buffer, 0, bufferSize);
                    if (result == -1 || result == 0) break;
                    fs.Write(buffer, 0, result);
                }

                fs.Flush();
                fs.Close();
                stream.Close();
                response.Close();

                Interface.Oxide.LogInfo($"Download of {filename} for completed successfully");
            }
            catch (Exception)
            {
                Interface.Oxide.LogError($"Couldn't download {filename}, please download manually from:\nhttps://dl.bintray.com/oxidemod/builds/{filename}");
            }
        }

        private static void SetCompilerVersion()
        {
            var version = FileVersionInfo.GetVersionInfo(BinaryPath);
            CompilerVersion = $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}";
            RemoteLogger.SetTag("compiler version", CompilerVersion);
        }

        private Process process;
        private readonly Regex fileErrorRegex = new Regex(@"([\w\.]+)\(\d+\,\d+\+?\): error|error \w+: Source file `[\\\./]*([\w\.]+)", RegexOptions.Compiled);
        private ObjectStreamClient<CompilerMessage> client;
        private Hash<int, Compilation> compilations;
        private Queue<CompilerMessage> messageQueue;
        private volatile int lastId;
        private volatile bool ready;
        private Core.Libraries.Timer.TimerInstance idleTimer;

        public PluginCompiler()
        {
            compilations = new Hash<int, Compilation>();
            messageQueue = new Queue<CompilerMessage>();
        }

        internal void Compile(CompilablePlugin[] plugins, Action<Compilation> callback)
        {
            var id = lastId++;
            var compilation = new Compilation(id, callback, plugins);
            compilations[id] = compilation;
            compilation.Prepare(() => EnqueueCompilation(compilation));
        }

        public void Shutdown()
        {
            ready = false;
            var endedProcess = process;
            if (endedProcess != null) endedProcess.Exited -= OnProcessExited;
            process = null;
            if (client == null) return;

            client.Message -= OnMessage;
            client.Error -= OnError;
            client.PushMessage(new CompilerMessage { Type = CompilerMessageType.Exit });
            client.Stop();
            client = null;
            if (endedProcess == null) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(5000);
                // Calling Close can block up to 60 seconds on certain machines
                if (!endedProcess.HasExited) endedProcess.Close();
            });
        }

        private void EnqueueCompilation(Compilation compilation)
        {
            if (compilation.plugins.Count < 1)
            {
                //Interface.Oxide.LogDebug("EnqueueCompilation called for an empty compilation");
                return;
            }

            if (!CheckCompiler())
            {
                OnCompilerFailed($"compiler v{CompilerVersion} couldn't be started");
                return;
            }

            compilation.Started();
            //Interface.Oxide.LogDebug("Compiling with references: {0}", compilation.references.Keys.ToSentence());
            var sourceFiles = compilation.plugins.SelectMany(plugin => plugin.IncludePaths).Distinct().Select(path => new CompilerFile(path)).ToList();
            sourceFiles.AddRange(compilation.plugins.Select(plugin => new CompilerFile($"{plugin.ScriptName}.cs", plugin.ScriptSource)));
            //Interface.Oxide.LogDebug("Compiling files: {0}", sourceFiles.Select(f => f.Name).ToSentence());
            var data = new CompilerData
            {
                OutputFile = compilation.name,
                SourceFiles = sourceFiles.ToArray(),
                ReferenceFiles = compilation.references.Values.ToArray()
            };
            var message = new CompilerMessage { Id = compilation.id, Data = data, Type = CompilerMessageType.Compile };
            if (ready)
                client.PushMessage(message);
            else
                messageQueue.Enqueue(message);
        }

        private void OnMessage(ObjectStreamConnection<CompilerMessage, CompilerMessage> connection, CompilerMessage message)
        {
            if (message == null)
            {
                Interface.Oxide.NextTick(() =>
                {
                    OnCompilerFailed($"compiler v{CompilerVersion} disconnected");
                    DependencyTrace();
                    Shutdown();
                });
                return;
            }

            switch (message.Type)
            {
                case CompilerMessageType.Assembly:
                    var compilation = compilations[message.Id];
                    if (compilation == null)
                    {
                        Interface.Oxide.LogWarning("Compiler compiled an unknown assembly"); // TODO: Any way to clarify this?
                        return;
                    }
                    compilation.endedAt = Interface.Oxide.Now;
                    var stdOutput = (string)message.ExtraData;
                    if (stdOutput != null)
                    {
                        foreach (var line in stdOutput.Split('\r', '\n'))
                        {
                            var match = fileErrorRegex.Match(line.Trim());
                            for (var i = 1; i < match.Groups.Count; i++)
                            {
                                var value = match.Groups[i].Value;
                                if (value.Trim() == string.Empty) continue;
                                var fileName = value.Basename();
                                var scriptName = fileName.Substring(0, fileName.Length - 3);
                                var compilablePlugin = compilation.plugins.SingleOrDefault(pl => pl.ScriptName == scriptName);
                                if (compilablePlugin == null)
                                {
                                    Interface.Oxide.LogError($"Unable to resolve script error to plugin: {line}");
                                    continue;
                                }
                                var missingRequirements = compilablePlugin.Requires.Where(name => !compilation.IncludesRequiredPlugin(name));
                                if (missingRequirements.Any())
                                    compilablePlugin.CompilerErrors = $"Missing dependencies: {missingRequirements.ToSentence()}";
                                else
                                    compilablePlugin.CompilerErrors = line.Trim().Replace(Interface.Oxide.PluginDirectory + Path.DirectorySeparatorChar, string.Empty);
                            }
                        }
                    }
                    compilation.Completed((byte[])message.Data);
                    compilations.Remove(message.Id);
                    idleTimer?.Destroy();
                    if (AutoShutdown)
                    {
                        Interface.Oxide.NextTick(() =>
                        {
                            idleTimer?.Destroy();
                            if (AutoShutdown) idleTimer = Interface.Oxide.GetLibrary<Core.Libraries.Timer>().Once(60, Shutdown);
                        });
                    }
                    break;
                case CompilerMessageType.Error:
                    Interface.Oxide.LogError("Compilation error: {0}", message.Data);
                    compilations[message.Id].Completed();
                    compilations.Remove(message.Id);
                    idleTimer?.Destroy();
                    if (AutoShutdown)
                    {
                        Interface.Oxide.NextTick(() =>
                        {
                            idleTimer?.Destroy();
                            idleTimer = Interface.Oxide.GetLibrary<Core.Libraries.Timer>().Once(60, Shutdown);
                        });
                    }
                    break;
                case CompilerMessageType.Ready:
                    connection.PushMessage(message);
                    if (!ready)
                    {
                        ready = true;
                        while (messageQueue.Count > 0) connection.PushMessage(messageQueue.Dequeue());
                    }
                    break;
            }
        }

        private static void OnError(Exception exception) => Interface.Oxide.LogException("Compilation error: ", exception);

        private bool CheckCompiler()
        {
            CheckCompilerBinary();
            idleTimer?.Destroy();

            if (BinaryPath == null) return false;
            if (process != null && process.Handle != IntPtr.Zero && !process.HasExited) return true;

            SetCompilerVersion();
            PurgeOldLogs();
            Shutdown();

            var args = new[] { "/service", "/logPath:" + EscapePath(Interface.Oxide.LogDirectory) };
            try
            {
                process = new Process
                {
                    StartInfo =
                    {
                        FileName = BinaryPath,
                        Arguments = string.Join(" ", args),
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true
                    },
                    EnableRaisingEvents = true
                };
                switch (Environment.OSVersion.Platform)
                {
                    case PlatformID.Win32S:
                    case PlatformID.Win32Windows:
                    case PlatformID.Win32NT:
                        process.StartInfo.EnvironmentVariables["PATH"] = $"{Path.Combine(Interface.Oxide.ExtensionDirectory, "x86")}";
                        break;
                    case PlatformID.Unix:
                    case PlatformID.MacOSX:
                        process.StartInfo.EnvironmentVariables["LD_LIBRARY_PATH"] = $"{Path.Combine(Interface.Oxide.ExtensionDirectory, IntPtr.Size == 8 ? "x64" : "x86")}";
                        break;
                }
                process.Exited += OnProcessExited;
                process.Start();
            }
            catch (Exception ex)
            {
                process?.Dispose();
                process = null;
                Interface.Oxide.LogException($"Exception while starting compiler v{CompilerVersion}: ", ex);
                if (BinaryPath.Contains("'")) Interface.Oxide.LogWarning("Server directory path contains an apostrophe, compiler will not work until path is renamed");
                else if (Environment.OSVersion.Platform == PlatformID.Unix) Interface.Oxide.LogWarning("Compiler may not be set as executable; chmod +x or 0744/0755 required");
                if (ex.GetBaseException() != ex) Interface.Oxide.LogException("BaseException: ", ex.GetBaseException());
                var win32 = ex as Win32Exception;
                if (win32 != null) Interface.Oxide.LogError("Win32 NativeErrorCode: {0} ErrorCode: {1} HelpLink: {2}", win32.NativeErrorCode, win32.ErrorCode, win32.HelpLink);
            }

            if (process == null) return false;

            client = new ObjectStreamClient<CompilerMessage>(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
            client.Message += OnMessage;
            client.Error += OnError;
            client.Start();
            return true;
        }

        private void OnProcessExited(object sender, EventArgs eventArgs)
        {
            Interface.Oxide.NextTick(() =>
            {
                OnCompilerFailed($"compiler v{CompilerVersion} was closed unexpectedly");
                if (Environment.OSVersion.Platform == PlatformID.Unix) Interface.Oxide.LogWarning("User running server may not have access to all service files");
                else Interface.Oxide.LogWarning("Compiler may have been closed by interference from security software");
                DependencyTrace();
                Shutdown();
            });
        }

        private void OnCompilerFailed(string reason)
        {
            foreach (var compilation in compilations.Values)
            {
                foreach (var plugin in compilation.plugins) plugin.CompilerErrors = reason;
                compilation.Completed();
            }
            compilations.Clear();
        }

        private static void PurgeOldLogs()
        {
            try
            {
                var filePaths = Directory.GetFiles(Interface.Oxide.LogDirectory, "*.txt").Where(f =>
                {
                    var fileName = Path.GetFileName(f);
                    return fileName != null && fileName.StartsWith("compiler_");
                });
                foreach (var filePath in filePaths) File.Delete(filePath);
            }
            catch (Exception) { }
        }

        private static string EscapePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "\"\"";

            path = Regex.Replace(path, @"(\\*)" + "\"", @"$1\$0");
            path = Regex.Replace(path, @"^(.*\s.*?)(\\*)$", "\"$1$2$2\"");
            return path;
        }

        private static class Algorithms
        {
            public static readonly HashAlgorithm MD5 = new MD5CryptoServiceProvider();
            public static readonly HashAlgorithm SHA1 = new SHA1Managed();
            public static readonly HashAlgorithm SHA256 = new SHA256Managed();
            public static readonly HashAlgorithm SHA384 = new SHA384Managed();
            public static readonly HashAlgorithm SHA512 = new SHA512Managed();
            public static readonly HashAlgorithm RIPEMD160 = new RIPEMD160Managed();
        }

        private static string GetChecksum(string filePath, HashAlgorithm algorithm)
        {
            using (var stream = new BufferedStream(File.OpenRead(filePath), 100000))
            {
                var hash = algorithm.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLower();
            }
        }

    }
}

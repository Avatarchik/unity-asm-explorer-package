﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AsmExplorer.Profiler
{
    static class ProfilerDataSerialization
    {
        public static unsafe void ReadProfilerTrace(ref ProfilerTrace trace, Stream stream, Allocator allocator)
        {

            var basePos = stream.Position;
            var reader = new RawReader(stream);
            ProfilerDataHeader header = default;
            reader.Read(&header);
            Debug.Assert(basePos + header.TotalLength <= stream.Length);
            Debug.Log($"header data: {header.NumSamples}, {header.NumFunctions}, {header.NumModules}, {header.NumThreads}, {header.NumStackFrames}");

            Read(ref trace.Samples, header.NumSamples, basePos + header.SamplesOffset);
            Read(ref trace.StackFrames, header.NumStackFrames, basePos + header.StackFramesOffset);
            Read(ref trace.Functions, header.NumFunctions, basePos + header.FunctionsOffset);
            Read(ref trace.Modules, header.NumModules, basePos + header.ModulesOffset);
            Read(ref trace.Threads, header.NumThreads, basePos + header.ThreadsOffset);
            return;

            void Read<T>(ref NativeArray<T> arr, int num, long offset) where T : unmanaged
            {
                stream.Position = basePos + offset;
                arr = new NativeArray<T>(num, allocator, NativeArrayOptions.UninitializedMemory);
                reader.ReadBytes(arr.GetUnsafePtr(), sizeof(T) * num);
            }
        }

        static ProcessIndex FindUnityProcessIndex(TraceProcesses processes)
        {
            foreach (var process in processes)
            {
                if (process.Name == "Unity" && !process.CommandLine.Contains("worker"))
                    return process.ProcessIndex;
            }

            return ProcessIndex.Invalid;
        }

        struct DiscoveredData<T>
        {
            public T Invalid;
            public List<T> Data;
            public Dictionary<T, int> Indices;

            public int Count => Data.Count;

            public int AddData(T val)
            {
                if (!Indices.TryGetValue(val, out int idx))
                {
                    if (Invalid.Equals(val))
                        return -1;
                    idx = Indices[val] = Data.Count;
                    Data.Add(val);
                }

                return idx;
            }

            public static DiscoveredData<T> Make(T invalid)
            {
                return new DiscoveredData<T>()
                {
                    Invalid = invalid,
                    Data = new List<T>(),
                    Indices = new Dictionary<T, int>()
                };
            }
        }

        struct DiscoveredFunction
        {
            public MonoJitInfo MonoMethod;
            public MethodIndex Index;

            public static DiscoveredFunction FromMethod(MonoJitInfo method) => new DiscoveredFunction
            {
                MonoMethod = method,
                Index = MethodIndex.Invalid,
            };

            public static readonly DiscoveredFunction Invalid = new DiscoveredFunction
            {
                MonoMethod = null,
                Index = MethodIndex.Invalid
            };
        }

        struct DiscoveredModule
        {
            public ModuleFileIndex Index;
            public Module MonoModule;

            public static DiscoveredModule FromIndex(ModuleFileIndex idx) => new DiscoveredModule
            {
                Index = idx,
            };

            public static DiscoveredModule FromMonoModule(Module asm) => new DiscoveredModule
            {
                MonoModule = asm,
                Index = ModuleFileIndex.Invalid
            };

            public static readonly DiscoveredModule Invalid = new DiscoveredModule
            {
                MonoModule = null,
                Index = ModuleFileIndex.Invalid
            };
        }


        public static unsafe void TranslateEtlFile(string etlPath, Stream stream)
        {
            var discoveredModules = DiscoveredData<DiscoveredModule>.Make(DiscoveredModule.Invalid);
            var discoveredStackFrames = DiscoveredData<CallStackIndex>.Make(CallStackIndex.Invalid);
            var discoveredFunctions = DiscoveredData<DiscoveredFunction>.Make(DiscoveredFunction.Invalid);
            var discoveredThreads = DiscoveredData<ThreadIndex>.Make(ThreadIndex.Invalid);

            var writer = new RawWriter(stream);
            var header = new ProfilerDataHeader
            {
                Version = 1,
            };
            long headerPos = stream.Position;
            writer.WriteBytes(&header, sizeof(ProfilerDataHeader));

            const string conv = "conversion.log";
            List<string> pdbWhitelist = new List<string>
            {
                "user32.dll",
                "kernelbase.dll",
                "wow64cpu.dll",
                "ntdll.dll",
                "unity.exe",
                "mono-2.0-bdwgc.dll",
                "d3d11.dll",
                "msvcrt.dll",
                "wow64.dll",
                "kernel32.dll",
                "ntoskrnl.exe",
            };
            if (File.Exists(conv))
                File.Delete(conv);
            var log = new StreamWriter(File.Open(conv, FileMode.Create, FileAccess.Write, FileShare.Read));
            var options = new TraceLogOptions()
            {
                ConversionLog = log,
                AlwaysResolveSymbols = true,
                LocalSymbolsOnly = false,
                AllowUnsafeSymbols = true,
#if UNITY_EDITOR
                AdditionalSymbolPath = Path.GetDirectoryName(EditorApplication.applicationPath),
#endif
                ShouldResolveSymbols = path =>
                {
                    path = path.ToLowerInvariant();
                    return pdbWhitelist.Any(x => path.EndsWith(x));
                }
            };
            using (var trace = TraceLog.OpenOrConvert(etlPath, options))
            {
                var processIndex = FindUnityProcessIndex(trace.Processes);
                var processId = trace.Processes[processIndex].ProcessID;

                {
                    header.SamplesOffset = stream.Position - headerPos;
                    SampleData sampleData = default;
                    int sampleCounter = 0;
                    foreach (var evt in trace.Events)
                    {
                        var sample = evt as SampledProfileTraceData;
                        if (sample == null || sample.ProcessID != processId)
                            continue;
                        sampleData.Address = (long)sample.InstructionPointer;
                        sampleData.ThreadIdx = discoveredThreads.AddData(sample.Thread()?.ThreadIndex ?? ThreadIndex.Invalid);
                        sampleData.TimeStamp = sample.TimeStampRelativeMSec;
                        sampleData.StackTrace = discoveredStackFrames.AddData(sample.CallStackIndex());
                        var codeAddress = sample.IntructionPointerCodeAddress();
                        sampleData.Function = GetFunctionIndex(codeAddress);
                        writer.Write(&sampleData);
                        sampleCounter++;
                    }

                    header.NumSamples = sampleCounter;
                }

                {
                    header.StackFramesOffset = stream.Position - headerPos;
                    StackFrameData stackTraceData = default;

                    // N.B. this loop adds more stack frames as it executes
                    for (int idx = 0; idx < discoveredStackFrames.Count; idx++)
                    {
                        var stack = trace.CallStacks[discoveredStackFrames.Data[idx]];
                        stackTraceData.Address = (long)stack.CodeAddress.Address;
                        stackTraceData.Depth = stack.Depth;
                        stackTraceData.CallerStackFrame = discoveredStackFrames.AddData(stack.Caller?.CallStackIndex ?? CallStackIndex.Invalid);
                        stackTraceData.Function = GetFunctionIndex(stack.CodeAddress);

                        writer.Write(&stackTraceData);
                    }

                    header.NumStackFrames = discoveredStackFrames.Count;
                }

                {
                    header.FunctionsOffset = stream.Position - headerPos;
                    FunctionData funcData = default;
                    foreach (var func in discoveredFunctions.Data)
                    {
                        if (func.Index != MethodIndex.Invalid)
                        {
                            var method = trace.CodeAddresses.Methods[func.Index];
                            funcData.BaseAddress = method.MethodRva;
                            funcData.Length = -1;
                            funcData.Module = discoveredModules.AddData(DiscoveredModule.FromIndex(method.MethodModuleFileIndex));
                            funcData.Name.CopyFrom(method.FullMethodName);
                        }
                        else
                        {
                            var jitData = func.MonoMethod;
                            funcData.BaseAddress = jitData.CodeStart.ToInt64();
                            funcData.Length = jitData.CodeSize;
                            funcData.Module = discoveredModules.AddData(DiscoveredModule.FromMonoModule(jitData.Method.Module));

                            var fullName = MonoFunctionName(jitData.Method);
                            funcData.Name.CopyFrom(fullName);
                        }

                        writer.Write(&funcData);
                    }

                    header.NumFunctions = discoveredFunctions.Count;
                }

                {
                    header.ModulesOffset = stream.Position - headerPos;
                    // make sure that all modules of the current process are included.
                    foreach (var module in trace.Processes[processIndex].LoadedModules)
                        discoveredModules.AddData(new DiscoveredModule { Index = module.ModuleFile.ModuleFileIndex });

                    ModuleData moduleData = default;
                    foreach (var dm in discoveredModules.Data)
                    {
                        if (dm.MonoModule != null)
                        {
                            moduleData = default;
                            moduleData.IsMono = true;
                            moduleData.FilePath = dm.MonoModule.Assembly.Location;
                        }
                        else
                        {
                            var module = trace.ModuleFiles[dm.Index];
                            moduleData.IsMono = false;
                            moduleData.Checksum = module.ImageChecksum;
                            moduleData.PdbAge = module.PdbAge;
                            moduleData.FilePath.CopyFrom(module.FilePath);
                            moduleData.PdbName.CopyFrom(module.PdbName);
                            var guidBytes = module.PdbSignature.ToByteArray();
                            fixed (byte* ptr = guidBytes)
                                UnsafeUtility.MemCpy(moduleData.PdbGuid, ptr, 16);
                        }

                        writer.Write(&moduleData);
                    }
                    header.NumModules = discoveredModules.Count + 1;
                }

                {
                    header.ThreadsOffset = stream.Position - headerPos;
                    ThreadData threadData = default;
                    foreach (var t in discoveredThreads.Data)
                    {
                        var thread = trace.Threads[t];
                        threadData.ThreadName.CopyFrom(thread.ThreadInfo ?? "");
                        writer.Write(&threadData);
                    }

                    header.NumThreads = discoveredThreads.Count;
                }
                stream.Flush();

                header.TotalLength = (int)(stream.Position - headerPos);
                stream.Position = headerPos;
                writer.Write(&header);
                stream.Flush();
            }

            options.ConversionLog.Close();
            options.ConversionLog.Dispose();

            int GetFunctionIndex(TraceCodeAddress address)
            {
                var method = address.Method;
                if (method == null)
                {
                    var jit = Mono.GetJitInfo(new IntPtr((long)address.Address));
                    if (jit.Method == null)
                        return -1;
                    return discoveredFunctions.AddData(DiscoveredFunction.FromMethod(jit));
                }
                return discoveredFunctions.AddData(new DiscoveredFunction
                {
                    Index = method.MethodIndex
                });
            }

            string MonoFunctionName(MethodBase method)
            {
                if (method == null)
                    return "???";
                return method.DeclaringType.Namespace + '.' + method.DeclaringType.Name + '.' + method.Name;
            }
        }
    }
}
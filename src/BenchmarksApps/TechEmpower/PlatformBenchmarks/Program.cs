// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
#if DATABASE
using Npgsql;
#endif
#if IOURING
using Kestrel.Transport.IoUring;
using Microsoft.Extensions.Logging;
#endif

namespace PlatformBenchmarks
{
    public class Program
    {
        public static string[] Args;

        public static async Task Main(string[] args)
        {
            Args = args;

            Console.WriteLine(Encoding.UTF8.GetString(BenchmarkApplication.ApplicationName));
#if !DATABASE
            Console.WriteLine(Encoding.UTF8.GetString(BenchmarkApplication.Paths.Plaintext));
            Console.WriteLine(Encoding.UTF8.GetString(BenchmarkApplication.Paths.Json));
#else
            Console.WriteLine(Encoding.UTF8.GetString(BenchmarkApplication.Paths.FortunesRaw));
            Console.WriteLine(Encoding.UTF8.GetString(BenchmarkApplication.Paths.FortunesDapper));
            Console.WriteLine(Encoding.UTF8.GetString(BenchmarkApplication.Paths.FortunesEf));
            Console.WriteLine(Encoding.UTF8.GetString(BenchmarkApplication.Paths.SingleQuery));
            Console.WriteLine(Encoding.UTF8.GetString(BenchmarkApplication.Paths.Updates));
            Console.WriteLine(Encoding.UTF8.GetString(BenchmarkApplication.Paths.MultipleQueries));
#endif
            DateHeader.SyncDateTimer();

            var host = BuildWebHost(args);
            var config = (IConfiguration)host.Services.GetService(typeof(IConfiguration));
            BatchUpdateString.DatabaseServer = config.Get<AppSettings>().Database;
#if DATABASE
            try
            {
                await BenchmarkApplication.RawDb.PopulateCache();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error trying to populate database cache: {ex}");
            }
#endif
            await host.RunAsync();
        }

        public static IWebHost BuildWebHost(string[] args)
        {
            Console.WriteLine($"BuildWebHost()");
            Console.WriteLine($"Args: {string.Join(' ', args)}");

            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
#if DEBUG || DEBUG_DATABASE
                .AddUserSecrets<Program>()
#endif
                .AddEnvironmentVariables()
                .AddEnvironmentVariables(prefix: "ASPNETCORE_")
                .AddCommandLine(args)
                .Build();

            var appSettings = config.Get<AppSettings>();
#if DATABASE
            Console.WriteLine($"Database: {appSettings.Database}");
            Console.WriteLine($"ConnectionString: {appSettings.ConnectionString}");

            if (appSettings.Database == DatabaseServer.PostgreSql)
            {
                BenchmarkApplication.RawDb = new RawDb(appSettings);
                BenchmarkApplication.DapperDb = new DapperDb(appSettings);
                BenchmarkApplication.EfDb = new EfDb(appSettings);
            }
            else
            {
                throw new NotSupportedException($"Database '{appSettings.Database}' is not supported, check your app settings.");
            }
#endif

            var hostBuilder = new WebHostBuilder()
                .UseBenchmarksConfiguration(config)
                .UseKestrel((context, options) =>
                {
                    var endPoints = context.Configuration.CreateIPEndPoints();

                    foreach (var endPoint in endPoints)
                    {
                        options.Listen(endPoint, builder =>
                        {
                            builder.UseHttpApplication<BenchmarkApplication>();
                        });
                    }
                })
                .UseStartup<Startup>();

#if IOURING
            // DEBUG: surface transport logger warnings/errors via Console when diagnosing io_uring regressions.
            if (Environment.GetEnvironmentVariable("IOURING_DEBUG_LOGS") == "1")
            {
                hostBuilder.ConfigureLogging(b =>
                {
                    b.ClearProviders();
                    b.AddConsole();
                    b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
                });
            }
#endif

#if IOURING
            if (Environment.GetEnvironmentVariable("USE_IOURING_TRANSPORT") != "0")
            {
                Console.WriteLine(">>> Using Kestrel.Transport.IoUring <<<");
                hostBuilder.UseIoUring(options =>
                {
                    if (int.TryParse(Environment.GetEnvironmentVariable("IOURING_THREAD_COUNT"), out var tc))
                        options.ThreadCount = tc;
                    else
                        options.ThreadCount = Environment.ProcessorCount;

                    if (int.TryParse(Environment.GetEnvironmentVariable("IOURING_RING_SIZE"), out var ringSize))
                        options.RingSize = ringSize;
                    if (int.TryParse(Environment.GetEnvironmentVariable("IOURING_MAX_CONNECTIONS"), out var maxConn))
                        options.MaxConnections = maxConn;

                    options.EnableBufferRing = Environment.GetEnvironmentVariable("IOURING_BUFRING") != "0";
                    options.EnableSqPoll = Environment.GetEnvironmentVariable("IOURING_SQPOLL") == "1";
                    // Note: EnableCoopTaskRun/EnableSingleIssuer/EnableDeferTaskRun were added post-v2.1.0.
                    // Round-2 local benchmarks showed all three are neutral or worse for our self-completing
                    // pattern, so we keep them off here. Re-enable via reflection if you publish a newer pkg.

                    Console.WriteLine($"    ThreadCount={options.ThreadCount}, RingSize={options.RingSize}, MaxConnections={options.MaxConnections}, BufRing={options.EnableBufferRing}, SqPoll={options.EnableSqPoll}");
                });
            }
            else
#endif
            {
                hostBuilder.UseSockets(options =>
                {
                    options.WaitForDataBeforeAllocatingBuffer = false;

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        options.UnsafePreferInlineScheduling = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS") == "1";
                    }
                });
            }

            var host = hostBuilder.Build();

            return host;
        }
    }
}

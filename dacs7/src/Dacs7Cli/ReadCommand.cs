﻿using Dacs7;
using Dacs7Cli.Options;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Dacs7Cli
{
    internal static class ReadCommand
    {
        internal static void Register(CommandLineApplication app)
        {
            app.Command("read", cmd =>
            {
                cmd.Description = "Read tags from the plc.";

                var addressOption = cmd.Option("-a | --address", "The IPAddress of the plc", CommandOptionType.SingleValue);
                var debugOption = cmd.Option("-d | --debug", "Activate debug output", CommandOptionType.NoValue);
                var traceOption = cmd.Option("-t | --trace", "Trace also dacs7 internals", CommandOptionType.NoValue);
                var registerOption = cmd.Option("-r | --register", "Register items for fast performing.", CommandOptionType.NoValue);
                var loopsOption = cmd.Option("-l | --loops", "Specify the number of read loops.", CommandOptionType.SingleValue);
                var waitOption = cmd.Option("-s | --wait", "Wait time between loops in ms.", CommandOptionType.SingleValue);
                var maxJobsOption = cmd.Option("-j | --jobs", "Maximum number of concurrent jobs.", CommandOptionType.SingleValue);

                var tagsArguments = cmd.Argument("tags", "Tags to read.", true);

                cmd.OnExecute(async () =>
                {
                    ReadOptions readOptions = null; ;
                    try
                    {
                        readOptions = new ReadOptions
                        {
                            Debug = debugOption.HasValue(),
                            Trace = traceOption.HasValue(),
                            Address = addressOption.HasValue() ? addressOption.Value() : "localhost",
                            RegisterItems = registerOption.HasValue(),
                            Loops = loopsOption.HasValue() ? Int32.Parse(loopsOption.Value()) : 1,
                            Wait = waitOption.HasValue() ? Int32.Parse(waitOption.Value()) : 0,
                            Tags = tagsArguments.Values,
                            MaxJobs = maxJobsOption.HasValue() ? Int32.Parse(maxJobsOption.Value()) : 10,
                        }.Configure();
                        var result = await Read(readOptions, readOptions.LoggerFactory);

                        await Task.Delay(500);

                        return result;
                    }
                    finally
                    {
                        readOptions?.LoggerFactory?.Dispose();
                    }
                });
            });
        }



        private static async Task<int> Read(ReadOptions readOptions, ILoggerFactory loggerFactory)
        {
            var client = new Dacs7Client(readOptions.Address, PlcConnectionType.Pg, 5000, loggerFactory)
            {
                MaxAmQCalled = (ushort)readOptions.MaxJobs,
                MaxAmQCalling = (ushort)readOptions.MaxJobs
            };
            var logger = loggerFactory?.CreateLogger("Dacs7Cli.Read");

            try
            {
                long msTotal = 0;
                await client.ConnectAsync();

                if (readOptions.RegisterItems)
                {
                    await client.RegisterAsync(readOptions.Tags);
                }

                for (int i = 0; i < readOptions.Loops; i++)
                {
                    if (i > 0 && readOptions.Wait > 0)
                    {
                        await Task.Delay(readOptions.Wait);
                    }

                    try
                    {
                        var sw = new Stopwatch();
                        sw.Start();
                        var results = await client.ReadAsync(readOptions.Tags);
                        sw.Stop();
                        msTotal += sw.ElapsedMilliseconds;
                        logger?.LogDebug($"ReadTime: {sw.Elapsed}");

                        var resultEnumerator = results.GetEnumerator();
                        foreach (var item in readOptions.Tags)
                        {
                            if (resultEnumerator.MoveNext())
                            {
                                logger?.LogInformation($"Read: {item}={resultEnumerator.Current.Data}   -  {resultEnumerator.Current.Value}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError($"Exception in loop {ex.Message}.");
                    }
                }

                if (readOptions.Loops > 0)
                {
                    logger?.LogInformation($"Average read time over loops is {msTotal / readOptions.Loops}ms");
                    await Task.Delay(readOptions.Wait);
                }
            }
            catch (Exception ex)
            {
                logger?.LogError($"An error occured in Read: {ex.Message} - {ex.InnerException?.Message}");
                return 1;
            }
            finally
            {
                if (readOptions.RegisterItems)
                {
                    await client.UnregisterAsync(readOptions.Tags);
                }

                await client.DisconnectAsync();
            }

            return 0;
        }

    }
}
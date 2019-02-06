// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Newtonsoft.Json;
using McMaster.Extensions.CommandLineUtils;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Jupyter.Core
{
    /// <summary>
    ///      The main application for Jupyter kernels, used both to install
    ///      kernelspecs into Jupyter and to start new kernel instances.
    /// </summary>
    public class KernelApplication : CommandLineApplication
    {
        private readonly KernelProperties properties;

        private readonly Action<ServiceCollection> configure;

        /// <summary>
        ///     Constructs a new application given properties describing a
        ///     particular kernel, and an action to configure services.
        /// </summary>
        /// <param name="properties">
        ///     Properties describing this kernel to clients.
        /// </param>
        /// <param name="configure">
        ///     An action to configure services for the new kernel application.
        ///     This action is called after all other kernel services have been
        ///     configured, and is typically used to provide an implementation
        ///     of <see cref="IExecutionEngine" /> along with any services
        ///     required by that engine.
        /// </param>
        /// <example>
        ///     To instantiate and run a kernel application using the
        ///     <c>EchoEngine</c> class:
        ///     <code>
        ///         public static int Main(string[] args) =>
        ///             new KernelApplication(
        ///                 properties,
        ///                 serviceCollection =>
        ///                      serviceCollection
        ///                     .AddSingleton&lt;IExecutionEngine, EchoEngine&gt;();
        ///             )
        ///             .WithDefaultCommands()
        ///             .Execute(args);
        ///         }
        ///     </code>
        /// </example>
        public KernelApplication(KernelProperties properties, Action<ServiceCollection> configure)
        {
            this.properties = properties;
            this.configure = configure;

            Name = $"dotnet {properties.KernelName}";
            Description = properties.Description;
            this.HelpOption();
            this.VersionOption(
                "--version",
                () => properties.KernelVersion,
                () =>
                    $"Language kernel: {properties.KernelVersion}\n" +
                    $"Jupyter core: {typeof(KernelApplication).Assembly.GetName().Version}"
            );
        }

        /// <summary>
        ///      <para>
        ///          Adds all default commands to this kernel application
        ///          (installation and kernel instantiation).
        ///      </para>
        ///      <seealso cref="AddInstallCommand" />
        ///      <seealso cref="AddKernelCommand" />
        /// </summary>
        public KernelApplication WithDefaultCommands() => this
            .AddInstallCommand()
            .AddKernelCommand();

        /// <summary>
        ///     Adds a command to allow users to install this kernel into
        ///     Jupyter's list of available kernels.
        /// </summary>
        /// <remarks>
        ///     This command assumes that the command <c>jupyter</c> is on the
        ///     user's <c>PATH</c>.
        /// </remarks>
        public KernelApplication AddInstallCommand()
        {
            this.Command(
                "install",
                cmd =>
                {
                    cmd.HelpOption();
                    cmd.Description = $"Installs the {properties.KernelName} kernel into Jupyter.";
                    var developOpt = cmd.Option(
                        "--develop",
                        "Installs a kernel spec that runs against this working directory. Useful for development only.",
                        CommandOptionType.NoValue
                    );
                    var logLevelOpt = cmd.Option<LogLevel>(
                        "-l|--log-level <LEVEL>",
                        "Level of logging messages to emit to the console. On development mode, defaults to Information.",
                        CommandOptionType.SingleValue
                    );
                    cmd.OnExecute(() =>
                    {
                        var develop = developOpt.HasValue();
                        var logLevel =
                            logLevelOpt.HasValue()
                            ? logLevelOpt.ParsedValue
                            : (develop ? LogLevel.Information : LogLevel.Error);
                        return ReturnExitCode(() => InstallKernelSpec(develop, logLevel));
                    });
                }
            );

            return this;
        }

        /// <summary>
        ///     Adds a command to allow Jupyter to start instances of this
        ///     kernel.
        /// </summary>
        /// <remarks>
        ///     This command is typically not run by end users directly, but
        ///     by Jupyter on the user's behalf.
        /// </remarks>
        public KernelApplication AddKernelCommand()
        {
            this.Command(
                "kernel",
                cmd =>
                {
                    cmd.HelpOption();
                    cmd.Description = $"Runs the {properties.KernelName} kernel. Typically only run by a Jupyter client.";
                    var connectionFileArg = cmd.Argument(
                        "connection-file", "Connection file used to connect to a Jupyter client."
                    );
                    var logLevelOpt = cmd.Option<LogLevel>(
                        "-l|--log-level <LEVEL>",
                        "Level of logging messages to emit to the console. Defaults to Error.", 
                        CommandOptionType.SingleValue
                    );
                    cmd.OnExecute(() =>
                    {
                        var connectionFile = connectionFileArg.Value;
                        var logLevel =
                            logLevelOpt.HasValue()
                            ? logLevelOpt.ParsedValue
                            : LogLevel.Error;

                        return ReturnExitCode(() => StartKernel(connectionFile, logLevel));
                    });
                }
            );

            return this;
        }

        /// <summary>
        ///      Given an action, runs the action and then returns with either
        ///      0 or a negative error code, depending on whether the action
        ///      completed successfully or threw an exception.
        /// </summary>
        /// <param name="func">An action to be run.</param>
        /// <returns>
        ///     Either <c>0</c> if <c>func</c> completed successfully
        ///     or <c>-1</c> if <c>func</c> threw an exception.
        /// </returns>
        public int ReturnExitCode(Action func)
        {
            try {
                func();
                return 0;
            } catch (Exception ex) {
                System.Console.Error.WriteLine(ex.Message);
                return -1;
            }
        }

        /// <summary>
        ///      Installs this kernel into Jupyter's list of available kernels.
        /// </summary>
        /// <param name="develop">
        ///      If <c>true</c>, this kernel will be installed in develop mode,
        ///      such that the kernel is rebuilt whenever a new instance is
        ///      started.
        /// </param>
        /// <param name="logLevel">
        ///      The default logging level to be used when starting new kernel
        ///      instances.
        /// </param>
        /// <remarks>
        ///      This method dynamically generates a new <c>kernelspec.json</c>
        ///      file representing the kernel properties provided when the
        ///      application was constructed, along with options such as the
        ///      development mode.
        /// </remarks>
        public int InstallKernelSpec(bool develop, LogLevel logLevel)
        {
            var kernelSpecDir = "";
            KernelSpec kernelSpec;
            if (develop)
            {
                System.Console.WriteLine(
                    "NOTE: Installing a kernel spec which references this directory.\n" +
                    $"      Any changes made in this directory will affect the operation of the {properties.FriendlyName} kernel.\n" +
                    "      If this was not what you intended, run 'dotnet " +
                              $"{properties.KernelName} install' without the '--develop' option."
                );

                // Serialize a new kernel spec that points to this directory.
                kernelSpec = new KernelSpec
                {
                    DisplayName = properties.KernelName,
                    LanguageName = properties.LanguageName,
                    Arguments = new List<string> {
                        "dotnet", "run",
                        "--project", Directory.GetCurrentDirectory(),
                        "--", "kernel",
                        "--log-level", logLevel.ToString(),
                        "{connection_file}"
                    }
                };
            }
            else
            {
                kernelSpec = new KernelSpec
                {
                    DisplayName = properties.DisplayName,
                    LanguageName = properties.LanguageName,
                    Arguments = new List<string>
                    {
                        "dotnet", properties.KernelName,
                        "kernel",
                        "--log-level", logLevel.ToString(),
                        "{connection_file}"
                    }
                };
            }

            // Make a temporary directory to hold the kernel spec.
            var tempKernelSpecDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var jsonPath = Path.Combine(tempKernelSpecDir, "kernel.json");
            Directory.CreateDirectory(tempKernelSpecDir);
            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(kernelSpec));
            kernelSpecDir = tempKernelSpecDir;

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "jupyter",
                Arguments = $"kernelspec install {kernelSpecDir} --name=\"{properties.KernelName}\""
            });
            process.WaitForExit();
            File.Delete(jsonPath);
            Directory.Delete(tempKernelSpecDir);
            return process.ExitCode;
        }


        /// <summary>
        ///     Launches a new kernel instance, loading all relevant connection
        ///     parameters from the given connection file as provided by
        ///     Jupyter.
        /// </summary>
        public int StartKernel(string connectionFile, LogLevel minLevel = LogLevel.Debug)
        {
            // Begin by setting up the dependency injection that we will need
            // in order to configure logging in a fashion that is idiomatic to
            // .NET Core.
            var serviceCollection = new ServiceCollection();
            serviceCollection
                // For now, we add a logger that reports to the console.
                // TODO: add a logger that reports back to the client.
                .AddLogging(configure => configure.AddConsole())
                .Configure<LoggerFilterOptions>(
                    options => options.MinLevel = minLevel
                )

                // We need to pass along the context to each server, including
                // information gleaned from the connection file and from user
                // preferences.
                .Configure<KernelContext>(
                    ctx =>
                    {
                        ctx.LoadConnectionFile(connectionFile);
                        ctx.Properties = properties;
                    }
                )

                // We want to make sure that we only ever start a single
                // copy of each listener.
                .AddSingleton<IHeartbeatServer, HeartbeatServer>()
                .AddSingleton<IShellServer, ShellServer>();

            // After setting up the service collection, we give the specific
            // kernel a chance to configure it. At a minimum, the specific kernel
            // must provide an IReplEngine.
            configure(serviceCollection);

            var serviceProvider = serviceCollection.BuildServiceProvider();

            // Minimally, we need to start a server for each of the heartbeat,
            // control and shell sockets.
            var logger = serviceProvider.GetService<ILogger<HeartbeatServer>>();
            var context = serviceProvider.GetService<KernelContext>();

            var heartbeatServer = serviceProvider.GetService<IHeartbeatServer>();
            var shellServer = serviceProvider.GetService<IShellServer>();
            var engine = serviceProvider.GetService<IExecutionEngine>();

            // We start by launching a heartbeat server, which echoes whatever
            // input it gets from the client. Clients can use this to ensure
            // that the kernel is still alive and responsive.
            engine.Start();
            heartbeatServer.Start();
            shellServer.Start();

            return 0;
        }
    }
}
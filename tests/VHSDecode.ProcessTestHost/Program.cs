using System.Diagnostics;
using System.Runtime.InteropServices;
using VHSDecode.Core.Processes;

switch (args[0])
{
    case "emit":
        for (int i = 0; i < 128; i++)
        {
            Console.Out.Write(new string('O', 4096));
            Console.Error.Write(new string('E', 4096));
        }
        return 7;
    case "sleep":
        File.WriteAllText(args[1], Environment.ProcessId.ToString());
        await Task.Delay(TimeSpan.FromMinutes(5));
        return 0;
    case "parent":
    case "launcher":
        using (PosixSignalRegistration? signal = args[0] == "launcher" && !OperatingSystem.IsWindows()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                Environment.Exit(0);
            })
            : null)
        {
            var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false
            };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            {
                startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            }
            startInfo.ArgumentList.Add("sleep");
            startInfo.ArgumentList.Add(args[1]);
            if (args[0] == "parent")
            {
                ExternalToolProcess.Configure(startInfo);
            }
            else
            {
                File.WriteAllText(args[1] + ".launcher", Environment.ProcessId.ToString());
            }
            using (Process child = Process.Start(startInfo)!)
            {
                await Task.Delay(TimeSpan.FromMinutes(5));
            }
        }
        return 0;
    case "copy":
        using (Stream input = Console.OpenStandardInput())
        using (Stream output = Console.OpenStandardOutput())
        {
            input.CopyTo(output);
        }
        return 0;
    case "crash":
        Environment.FailFast("Intentional process test host crash for .NET diagnostics verification.");
        return 0;
    default:
        throw new ArgumentException("Unknown process test scenario.");
}

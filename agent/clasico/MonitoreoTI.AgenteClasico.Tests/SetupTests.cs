using System;
using System.Runtime.InteropServices;
using MonitoreoTI.AgenteClasico;
using Xunit;

namespace MonitoreoTI.AgenteClasico.Tests
{
    public class SetupTests
    {
        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        /// <summary>Cómo separa Windows (y sc.exe) la línea de comandos.</summary>
        private static string[] Split(string commandLine)
        {
            var pointer = CommandLineToArgvW("sc.exe " + commandLine, out var count);
            try
            {
                var args = new string[count - 1];
                for (var i = 1; i < count; i++)
                    args[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size));
                return args;
            }
            finally
            {
                LocalFree(pointer);
            }
        }

        [Fact]
        public void The_service_binpath_reaches_sc_whole_with_its_quotes_and_argument()
        {
            var binPath = "\"C:\\Program Files\\MonitoreoTI\\AgenteClasico\\MonitoreoTI.AgenteClasico.exe\" servicio";
            var line = Setup.CommandLine("create", "MonitoreoTIAgent", "binPath=", binPath, "start=", "auto", "DisplayName=", "MonitoreoTI Agente (clásico)");

            Assert.Equal(new[] { "create", "MonitoreoTIAgent", "binPath=", binPath, "start=", "auto", "DisplayName=", "MonitoreoTI Agente (clásico)" }, Split(line));
        }
    }
}

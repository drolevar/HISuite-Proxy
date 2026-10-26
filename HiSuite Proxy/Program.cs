using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HiSuite_Proxy
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static int Main(string[] arguments)
        {
            // Keep inspection and offline preparation free of Form1 startup effects
            // (certificate installation and proxy listener creation).
            if (arguments.Length > 0 && arguments[0].StartsWith("--", StringComparison.Ordinal))
                return RunHiSuite14Command(arguments);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1(arguments));
            return 0;
        }

        private static int RunHiSuite14Command(string[] arguments)
        {
            try
            {
                HiSuite14PatchStatus status;
                switch (arguments[0])
                {
                    case "--hisuite14-inspect":
                        if (arguments.Length != 2) return PrintHiSuite14Usage();
                        status = HiSuite14Patcher.Inspect(arguments[1]);
                        break;
                    case "--hisuite14-prepare":
                        if (arguments.Length != 3) return PrintHiSuite14Usage();
                        status = HiSuite14Patcher.Prepare(arguments[1], arguments[2]);
                        break;
                    case "--hisuite14-apply":
                        if (arguments.Length != 2) return PrintHiSuite14Usage();
                        status = HiSuite14Patcher.Apply(arguments[1]);
                        break;
                    case "--hisuite14-restore":
                        if (arguments.Length != 2) return PrintHiSuite14Usage();
                        status = HiSuite14Patcher.Restore(arguments[1]);
                        break;
                    default:
                        return PrintHiSuite14Usage();
                }
                Console.WriteLine(status.ToString());
                return status.IsSupported ? 0 : 1;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        private static int PrintHiSuite14Usage()
        {
            Console.Error.WriteLine("Usage: \"HiSuite Proxy.exe\" --hisuite14-inspect|--hisuite14-apply|--hisuite14-restore <HiSuite directory>");
            Console.Error.WriteLine("       \"HiSuite Proxy.exe\" --hisuite14-prepare <source directory> <new output directory>");
            return 2;
        }
    }
}

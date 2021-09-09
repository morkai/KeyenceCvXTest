using CVXLib;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;

namespace KeyenceCvXTest
{
    class Program
    {
        static int TRIGGER_TIME = 5000;
        static int TRIGGER_SLEEP = 100;

        static bool debug = false;
        static int repeat = 0;
        static string host = "192.168.1.233";
        static ushort port = 8502;
        static ushort program = 0;
        static bool inlineImage = false;
        static bool reset = false;

        static bool cancelled = false;
        static string resultPath = "";
        static bool resultAvailable = false;
        static bool imageAvailable = false;
        static CVX client;

        static void ParseArgs(string[] args)
        {
            for (var i = 0; i < args.Length; ++i)
            {
                var k = args[i];
                
                switch (k)
                {
                    case "--debug":
                        debug = args[++i] == "1";
                        break;

                    case "--repeat":
                        repeat = int.Parse(args[++i]);

                        if (repeat < 0)
                        {
                            throw new Exception("--repeat must be greater than or equal to 0.");
                        }
                        break;

                    case "--host":
                        host = args[++i];
                        break;

                    case "--port":
                        port = ushort.Parse(args[++i]);

                        if (port <= 0 || port > 65535)
                        {
                            throw new Exception("--port must be between 1 and 65535.");
                        }
                        break;

                    case "--program":
                        program = ushort.Parse(args[++i]);

                        if (program < 0 || program > 31)
                        {
                            throw new Exception("--program must be between 0 and 31.");
                        }
                        break;

                    case "--inline-image":
                        inlineImage = args[++i] == "1";
                        break;

                    case "--reset":
                        reset = args[++i] == "1";
                        break;
                }
            }
        }

        static void Main(string[] args)
        {
            try
            {
                ParseArgs(args);
            }
            catch (Exception x)
            {
                ExitWithError("ERR_INVALID_ARGS", $"Failed to parse arguments: {x.Message}");
            }

            Console.CancelKeyPress += Console_CancelKeyPress;

            Console.Error.WriteLine("Setting up the output directory...");

            string outputPath = Path.Combine(Path.GetTempPath(), "KeyenveCvXTest");

            try
            {
                if (Directory.Exists(outputPath))
                {
                    Directory.Delete(outputPath, true);
                }

                Directory.CreateDirectory(outputPath);
            }
            catch (Exception x)
            {
                ExitWithError("ERR_OUTPUT_DIR_SETUP", $"Failed to setup the output directory: {x.Message}");
            }

            client = new CVX()
            {
                Address = host,
                Port = port
            };

            client.OnResultLogDataReceived += Client_OnResultLogDataReceived;
            client.OnImageLogDataReceived += Client_OnImageLogDataReceived;

            client.Initialize();

            Console.Error.WriteLine($"Connecting to {host}:{port}...");

            int connected = client.Connect();

            if (connected != 0)
            {
                ExitWithError("ERR_CONNECTION_FAILURE", $"Failed to connect: {connected}");
            }

            int resultLogStarted = client.StartResultLog(0, outputPath);

            if (resultLogStarted != 0)
            {
                ExitWithError("ERR_RESULT_LOG_FAILURE", $"Failed to start the result log: {resultLogStarted}");
            }

            int imageLogStarted = client.StartImageLog(outputPath);

            if (imageLogStarted != 0)
            {
                ExitWithError("ERR_IMAGE_LOG_FAILURE", $"Failed to start the image log: {imageLogStarted}");
            }

            while (!IsCancelled())
            {
                try
                {
                    Run();
                }
                catch (Exception x)
                {
                    if (IsCancelled())
                    {
                        Console.Error.WriteLine("ERR_CANCELLED");
                    }
                    else if (x.Message.StartsWith("ERR_"))
                    {
                        Console.Error.WriteLine(x.Message);
                    }
                    else
                    {
                        Console.Error.WriteLine(x.Message);
                        Console.Error.WriteLine("ERR_EXCEPTION");
                    }

                    if (repeat == 0)
                    {
                        Environment.Exit(1);
                    }
                }

                if (repeat == 0)
                {
                    break;
                }

                if (!IsCancelled())
                {
                    Thread.Sleep(repeat);
                }

                Console.Error.WriteLine();
            }

            if (client.Connected)
            {
                Console.Error.WriteLine("Closing the connection...");

                if (client.ResultLogStarted)
                {
                    client.StopResultLog();
                }

                if (client.ImageLogStarted)
                {
                    client.StopImageLog();
                }

                client.Disconnect();
            }

            if (resultAvailable && imageAvailable)
            {
                OutputResult(outputPath);
            }
        }

        private static void ExitWithError(string errorCode, string errorMessage = "")
        {
            if (client != null && client.Connected)
            {
                client.Disconnect();
            }

            if (!String.IsNullOrEmpty(errorMessage))
            {
                Console.Error.WriteLine(errorMessage);
            }

            Console.Error.WriteLine(errorCode);

            Environment.Exit(1);
        }

        private static void Client_OnResultLogDataReceived(int state, int driveNo, int settingNo, string dstFile)
        {
            resultAvailable = true;
            resultPath = dstFile;
        }

        private static void Client_OnImageLogDataReceived(int state, int driveNo, int settingNo, int conditionType, int prcCount)
        {
            imageAvailable = true;
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cancelled = true;
        }

        private static bool IsCancelled()
        {
            return cancelled;
        }

        private static void Run()
        {
            CheckInitialConditions();
            ResetState();
            SelectProgram();
            Trigger();

            if (!debug)
            {
                ResetState();
            }
        }

        private static string ExecuteCommand(string command)
        {
            if (IsCancelled())
            {
                throw new Exception();
            }

            string response = "";
            int result = client.ExecuteCommand(command, ref response);

            if (result != 0)
            {
                if (string.IsNullOrEmpty(response))
                {
                    response = "?";
                }

                ExitWithError("ERR_COMMAND_FAILURE", $"Command {command} failed: ({result}) {response}");
            }

            return response;
        }

        private static void ResetState()
        {
            Console.Error.WriteLine("Resetting state...");

            if (reset)
            {
                ExecuteCommand("RS"); // Reset
            }
            else
            {
                ExecuteCommand("CE"); // Clear error
            }
        }

        private static void CheckInitialConditions()
        {
            Console.Error.WriteLine("Checking initial conditions...");

            var modeRes = ExecuteCommand("RM"); // Read run/setup mode

            if (String.Equals(modeRes, "RM,0"))
            {
                Console.Error.WriteLine("Switching to run mode...");

                ExecuteCommand("R0"); // Switch to run mode
            }
        }

        private static void SelectProgram()
        {
            Console.Error.WriteLine($"Selecting program no. {program}...");

            var programNo = program.ToString().PadLeft(3, '0');
            var programRes = ExecuteCommand("PR"); // Read program setting

            if (String.Equals(programRes, $"PR,1,{programNo}"))
            {
                Console.Error.WriteLine("Program already selected.");

                return;
            }

            ExecuteCommand($"PW,1,{programNo}"); // Change programs
        }

        private static void Trigger()
        {
            Console.Error.WriteLine("Triggering...");

            ExecuteCommand("TA"); // Issue all triggers

            var startedAt = DateTime.Now;
            var wasResultAvailable = false;

            resultAvailable = false;
            imageAvailable = false;

            while (!IsCancelled() && DateTime.Now.Subtract(startedAt).TotalMilliseconds <= TRIGGER_TIME)
            {
                if (resultAvailable)
                {
                    if (!wasResultAvailable)
                    {
                        wasResultAvailable = true;

                        Console.Error.WriteLine("...result available!");
                    }

                    if (imageAvailable)
                    {
                        Console.Error.WriteLine("...image available!");

                        return;
                    }
                    else
                    {
                        Console.Error.WriteLine("...image not available...");
                    }
                }
                else
                {
                    Console.Error.WriteLine("...result not available...");
                }

                Thread.Sleep(TRIGGER_SLEEP);
            }

            throw new Exception("ERR_RESULT_NOT_AVAILABLE");
        }

        private static void OutputResult(string outputPath)
        {
            try
            {
                var json = new StringBuilder();
                var programOutput = false;
                var resultOutput = false;

                var resultText = File.ReadAllText(resultPath).Trim();
                var resultParts = resultText.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)[0].Split(',');

                foreach (var resultPart in resultParts)
                {
                    var paramParts = resultPart.Trim().Split(new char[] { '=' }, 2);
                    var k = paramParts[0].Trim();
                    var v = paramParts.Length > 1 ? paramParts[1].Trim() : "";

                    if (String.Equals(k, "program", StringComparison.InvariantCultureIgnoreCase))
                    {
                        programOutput = true;

                        json.Append(@",""program"":");
                        json.Append(int.Parse(v));

                        continue;
                    }

                    if (String.Equals(k, "result", StringComparison.InvariantCultureIgnoreCase) || String.Equals(k, "pass", StringComparison.InvariantCultureIgnoreCase))
                    {
                        resultOutput = true;

                        json.Append(@",""result"":");
                        json.Append(string.Equals(v, "0") ? "true" : "false");

                        continue;
                    }

                    json.Append($",\"{k}\":");

                    if (String.IsNullOrWhiteSpace(v))
                    {
                        json.Append("\"\"");

                        continue;
                    }

                    if (Regex.IsMatch(v, "^[0-9]+(\\.[0-9]+)?$"))
                    {
                        v = v.Trim('0');

                        if (v.StartsWith("."))
                        {
                            v = $"0{v}";
                        }

                        if (v.EndsWith("."))
                        {
                            v += "0";
                        }
                        
                        if (string.Equals(v, string.Empty) || string.Equals(v, "0.0"))
                        {
                            v = "0";
                        }

                        json.Append(decimal.Parse(v, NumberStyles.Number));
                    }
                    else
                    {
                        json.Append(HttpUtility.JavaScriptStringEncode(v, true));
                    }
                }

                if (!programOutput)
                {
                    json.Append(@",""program"":");
                    json.Append(program);
                }

                if (!resultOutput)
                {
                    json.Append(@",""result"":");
                    json.Append("false");
                }

                OutputImage(json, outputPath);

                json.Replace(',', '{', 0, 1);
                json.Append("}");

                Console.WriteLine(json.ToString());
            }
            catch (Exception x)
            {
                Console.Error.WriteLine(x.ToString());
                ExitWithError("ERR_RESULT_FAILURE", $"Failed to output the result: {x.Message}");
            }
        }

        private static void OutputImage(StringBuilder json, string outputPath)
        {
            var files = Directory.GetFiles(outputPath.Replace(".txt", ""), "*.jpg", SearchOption.AllDirectories);

            json.Append(@",""image"":");

            if (files.Length == 0)
            {
                json.Append("null");

                return;
            }

            var imagePath = files[0];
            var imageData = inlineImage ? Convert.ToBase64String(File.ReadAllBytes(imagePath)) : imagePath;

            json.Append(HttpUtility.JavaScriptStringEncode(imageData, true));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Cfex
{
    class Program
    {
        private static ModuleDefMD module;
        public static int amout;
        private static BlocksCflowDeobfuscator CfDeob = new BlocksCflowDeobfuscator();
        private static FieldDef field;

        static void Main(string[] args)
        {
            // Kiểm tra nếu không có tham số, sử dụng đường dẫn mặc định
            if (args.Length == 0)
            {
                args = new string[] { "D:\\Desktop\\ee\\Metasoft.exe" };
            }

            string path = args[0];

            // Kiểm tra xem file có tồn tại không
            if (!File.Exists(path))
            {
                Console.ForegroundColor = ConsoleColor.Red; // Màu đỏ cho lỗi
                Console.WriteLine($"Lỗi: File không tồn tại tại đường dẫn {path}");
                Console.ResetColor(); // Khôi phục màu mặc định
                Console.ReadLine();
                return;
            }

            // Kiểm tra quyền đọc file
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    // Không cần đóng thủ công, using sẽ tự xử lý
                }
            }
            catch (UnauthorizedAccessException)
            {
                Console.ForegroundColor = ConsoleColor.Red; // Màu đỏ cho lỗi
                Console.WriteLine($"Lỗi: Không có quyền truy cập file {path}. Vui lòng chạy chương trình với quyền Administrator.");
                Console.ResetColor(); // Khôi phục màu mặc định
                Console.ReadLine();
                return;
            }
            catch (IOException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red; // Màu đỏ cho lỗi
                Console.WriteLine($"Lỗi: Không thể mở file {path}. Lỗi: {ex.Message}");
                Console.ResetColor(); // Khôi phục màu mặc định
                Console.ReadLine();
                return;
            }

            try
            {
                module = ModuleDefMD.Load(path);
                Console.ForegroundColor = ConsoleColor.Green; // Màu xanh lá cây
                Console.WriteLine("Module Loaded");
                Console.ResetColor();

                Stopwatch sw = Stopwatch.StartNew();
                cleaner();
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Green; // Màu xanh lá cây
                Console.WriteLine("Time taken: {0}ms", sw.Elapsed.TotalMilliseconds);
                Console.ResetColor();

                ModuleWriterOptions mod = new ModuleWriterOptions(module);
                mod.MetadataLogger = DummyLogger.NoThrowInstance;
                Console.ForegroundColor = ConsoleColor.Green; // Màu xanh lá cây
                Console.WriteLine("Cases fixed: " + amout);
                Console.ResetColor();

                string path2 = Path.GetDirectoryName(path) + "\\" + Path.GetFileNameWithoutExtension(path) + "-cleaned" + Path.GetExtension(path);
                if (module.IsILOnly)
                {
                    module.Write(path2, mod);
                    // Kiểm tra file đầu ra bằng PEVerify nếu có
                    VerifyAssembly(path2);
                }
                else
                {
                    NativeModuleWriterOptions mod2 = new NativeModuleWriterOptions(module, false);
                    mod2.MetadataLogger = DummyLogger.NoThrowInstance;
                    module.NativeWrite(path2, mod2);
                    VerifyAssembly(path2);
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red; // Màu đỏ cho lỗi
                Console.WriteLine($"Lỗi khi xử lý file: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                Console.ResetColor(); // Khôi phục màu mặc định
            }

            Console.ReadLine();
        }

        private static void VerifyAssembly(string path)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("peverify.exe", $"\"{path}\"");
                psi.RedirectStandardOutput = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    Console.ForegroundColor = ConsoleColor.Green; // Màu xanh lá cây
                    Console.WriteLine($"PEVerify output for {path}: {output}");
                    if (output.Contains("All classes and methods in"))
                    {
                        Console.WriteLine("Assembly verified successfully.");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow; // Màu vàng cho cảnh báo
                        Console.WriteLine("Warning: Assembly verification failed. Check PEVerify output for details.");
                        Console.ResetColor();
                    }
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red; // Màu đỏ cho lỗi
                Console.WriteLine($"Error running PEVerify on {path}: {ex.Message}");
                Console.ResetColor();
            }
        }

        public static void DeobfuscateCflow(MethodDef meth)
        {
            if (meth.Body == null || !meth.HasBody)
                return;

            try
            {
                // Lấy danh sách tham số của method
                IList<Parameter> parameters = meth.Parameters;

                // Tối ưu hóa body trước khi deobfuscate
                meth.Body.SimplifyMacros(parameters);
                meth.Body.SimplifyBranches();
                meth.Body.OptimizeMacros();

                Blocks blocks = new Blocks(meth);
                List<Block> test = blocks.MethodBlocks.GetAllBlocks();
                blocks.RemoveDeadBlocks();
                blocks.RepartitionBlocks();
                blocks.UpdateBlocks();

                BlocksCflowDeobfuscator CfDeob = new BlocksCflowDeobfuscator();
                CfDeob.Initialize(blocks);
                CfDeob.Add(new Cflow());
                CfDeob.Deobfuscate();

                // Kiểm tra và tối ưu hóa lại sau deobfuscate
                blocks.RepartitionBlocks();
                blocks.Method.Body.SimplifyBranches();
                blocks.Method.Body.OptimizeMacros();

                // Lấy code và exception handlers
                blocks.GetCode(out var instructions, out var exceptionHandlers);

                // Xác thực IL trước khi khôi phục
                if (IsValidBody(meth, instructions, exceptionHandlers))
                {
                    DotNetUtils.RestoreBody(meth, instructions, exceptionHandlers);
                    Console.ForegroundColor = ConsoleColor.Green; // Màu xanh lá cây
                    Console.WriteLine($"Successfully deobfuscated method: {meth.FullName}");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow; // Màu vàng cho cảnh báo
                    Console.WriteLine($"Warning: Method {meth.FullName} has invalid IL after deobfuscate. Skipping restoration.");
                    Console.ResetColor();
                }
            }
            catch (OverflowException ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow; // Màu vàng cho cảnh báo
                Console.WriteLine($"Overflow error deobfuscating method {meth.FullName}: {ex.Message}. Skipping method.");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red; // Màu đỏ cho lỗi
                Console.WriteLine($"Error deobfuscating method {meth.FullName}: {ex.Message}. Skipping method.");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                Console.ResetColor();
            }
        }

        public static bool hasCflow(MethodDef methods)
        {
            if (methods.Body == null || methods.Body.Instructions == null)
                return false;

            for (int i = 0; i < methods.Body.Instructions.Count; i++)
            {
                if (methods.Body.Instructions[i]?.OpCode == OpCodes.Switch)
                {
                    return true;
                }
            }
            return false;
        }

        public static void cleaner()
        {
            if (module == null)
            {
                Console.ForegroundColor = ConsoleColor.Red; // Màu đỏ cho lỗi
                Console.WriteLine("Error: Module is null in cleaner");
                Console.ResetColor();
                return;
            }

            foreach (TypeDef types in module.GetTypes())
            {
                foreach (MethodDef methods in types.Methods)
                {
                    if (methods.HasBody && hasCflow(methods))
                    {
                        Console.ForegroundColor = ConsoleColor.Green; // Màu xanh lá cây
                        Console.WriteLine($"Processing method with control flow: {methods.FullName}");
                        Console.ResetColor();
                        DeobfuscateCflow(methods);
                    }
                }
            }
        }

        public static bool IsValidBody(MethodDef meth, IList<Instruction> instructions, IList<ExceptionHandler> exceptionHandlers)
        {
            if (instructions == null || instructions.Count == 0)
                return false;

            // Kiểm tra cơ bản: không có nhánh không hợp lệ, instruction hợp lệ, v.v.
            foreach (var instr in instructions)
            {
                if (instr == null || instr.OpCode == null)
                    return false;

                // Kiểm tra các nhánh (branch) có hợp lệ không
                if (instr.OpCode.FlowControl == FlowControl.Branch || instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    if (instr.Operand == null || !(instr.Operand is Instruction))
                        return false;
                }
            }

            // Kiểm tra exception handlers
            if (exceptionHandlers != null)
            {
                foreach (var handler in exceptionHandlers)
                {
                    if (handler.TryStart == null || handler.TryEnd == null || handler.HandlerStart == null || handler.HandlerEnd == null)
                        return false;
                }
            }

            return true;
        }
    }
}
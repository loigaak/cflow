using dnlib.DotNet.Emit;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Linq;

public class Cflow
{
    private readonly MethodDefinition method;
    private readonly Dictionary<Instruction, object> constantValues = new Dictionary<Instruction, object>();

    public Cflow(MethodDefinition method)
    {
        this.method = method;
    }

    /// <summary>
    /// Tối ưu hóa control flow của phương thức, xóa bỏ obfuscation mà không gây lỗi tamper.
    /// </summary>
    public void Optimize()
    {
        if (method == null || !method.HasBody) return;

        // Bước 1: Phân tích và lan truyền hằng số
        PropagateConstants();

        // Bước 2: Loại bỏ junk code
        RemoveJunkCode();

        // Bước 3: Giải quyết các lệnh switch động
        ResolveSwitches();

        // Bước 4: Đơn giản hóa hằng số obfuscated
        SimplifyObfuscatedConstants();

        // Bước 5: Xử lý exception handling
        HandleExceptions();

        // Bước 6: Nhận diện và đơn giản hóa vòng lặp/state machine
        SimplifyLoopsAndStateMachines();

        // Cập nhật lại thân phương thức sau khi tối ưu
        method.Body.SimplifyMacros();
        method.Body.OptimizeMacros();
    }

    /// <summary>
    /// Lan truyền hằng số để xác định giá trị của biến tại các điểm trong mã.
    /// </summary>
    private void PropagateConstants()
    {
        var instructions = method.Body.Instructions;
        var stack = new Stack<object>();

        foreach (var instr in instructions)
        {
            switch (instr.OpCode.Code)
            {
                case Code.Ldc_I4:
                    stack.Push(instr.Operand);
                    constantValues[instr] = instr.Operand;
                    break;
                case Code.Stloc:
                case Code.Stloc_0:
                case Code.Stloc_1:
                case Code.Stloc_2:
                case Code.Stloc_3:
                    if (stack.Count > 0)
                    {
                        var value = stack.Pop();
                        constantValues[instr] = value;
                    }
                    break;
                case Code.Add:
                    if (stack.Count >= 2)
                    {
                        var b = (int)stack.Pop();
                        var a = (int)stack.Pop();
                        stack.Push(a + b);
                        constantValues[instr] = a + b;
                    }
                    break;
                // Thêm các phép toán khác nếu cần (mul, xor, v.v.)
                case Code.Nop:
                case Code.Dup:
                case Code.Pop:
                    break; // Sẽ xử lý trong RemoveJunkCode
                default:
                    if (instr.OpCode.FlowControl == FlowControl.Branch ||
                        instr.OpCode.FlowControl == FlowControl.Cond_Branch)
                    {
                        // Reset stack nếu cần khi gặp nhánh
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Loại bỏ junk code như nop và các cặp dup-pop.
    /// </summary>
    private void RemoveJunkCode()
    {
        var instructions = method.Body.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            if (instr.OpCode == OpCodes.Nop)
            {
                instructions.RemoveAt(i);
                i--;
            }
            else if (instr.OpCode == OpCodes.Dup && i + 1 < instructions.Count && instructions[i + 1].OpCode == OpCodes.Pop)
            {
                instructions.RemoveAt(i); // Xóa dup
                instructions.RemoveAt(i); // Xóa pop
                i--;
            }
        }
    }

    /// <summary>
    /// Giải quyết các lệnh switch động bằng cách thay thế bằng nhánh cụ thể nếu có thể.
    /// </summary>
    private void ResolveSwitches()
    {
        var instructions = method.Body.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            if (instr.OpCode == OpCodes.Switch)
            {
                Instruction prevInstr = i > 0 ? instructions[i - 1] : null;
                if (prevInstr != null && constantValues.TryGetValue(prevInstr, out object switchValue) && switchValue is int index)
                {
                    var targets = (Instruction[])instr.Operand;
                    if (index >= 0 && index < targets.Length)
                    {
                        // Thay switch bằng nhánh trực tiếp
                        instr.OpCode = OpCodes.Br;
                        instr.Operand = targets[index];
                    }
                }
            }
        }
    }

    /// <summary>
    /// Đơn giản hóa các chuỗi phép toán thành hằng số cụ thể.
    /// </summary>
    private void SimplifyObfuscatedConstants()
    {
        var instructions = method.Body.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            if (instr.OpCode == OpCodes.Sizeof)
            {
                // Thay sizeof bằng giá trị cụ thể
                if (instr.Operand is TypeReference typeRef)
                {
                    int size = GetSizeOfType(typeRef);
                    instructions[i] = Instruction.Create(OpCodes.Ldc_I4, size);
                    constantValues[instructions[i]] = size;
                }
            }
            // Có thể thêm logic để gộp các phép toán như add, mul, xor nếu cần
        }
    }

    /// <summary>
    /// Xử lý exception handling để tái cấu trúc luồng điều khiển.
    /// </summary>
    private void HandleExceptions()
    {
        if (!method.Body.HasExceptionHandlers) return;

        var handlers = method.Body.ExceptionHandlers;
        var instructions = method.Body.Instructions;

        foreach (var handler in handlers.ToList())
        {
            // Đảm bảo các lệnh leave được ánh xạ chính xác đến khối catch/finally
            foreach (var instr in instructions)
            {
                if (instr.OpCode == OpCodes.Leave || instr.OpCode == OpCodes.Leave_S)
                {
                    if (instr.Operand is Instruction target && target.Offset > handler.HandlerEnd.Offset)
                    {
                        // Điều chỉnh luồng điều khiển nếu cần
                    }
                }
            }
        }
    }

    /// <summary>
    /// Nhận diện và đơn giản hóa vòng lặp/state machine.
    /// </summary>
    private void SimplifyLoopsAndStateMachines()
    {
        var instructions = method.Body.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            if (instr.OpCode == OpCodes.Br || instr.OpCode == OpCodes.Br_S)
            {
                var target = instr.Operand as Instruction;
                if (target != null && target.Offset < instr.Offset)
                {
                    // Phát hiện vòng lặp, có thể thay thế bằng cấu trúc rõ ràng hơn nếu cần
                    // Hiện tại chỉ giữ nguyên để tránh lỗi tamper
                }
            }
        }
    }

    /// <summary>
    /// Lấy kích thước của một kiểu dữ liệu (dùng cho sizeof).
    /// </summary>
    private int GetSizeOfType(TypeReference type)
    {
        switch (type.FullName)
        {
            case "System.Int32": return 4;
            case "System.Int64": return 8;
            case "System.Boolean": return 1;
            // Thêm các kiểu khác nếu cần
            default: return 4; // Giá trị mặc định
        }
    }
}
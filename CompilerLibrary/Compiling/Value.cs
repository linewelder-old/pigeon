﻿namespace CompilerLibrary.Compiling;

internal record Value(TypeInfo? Type)
{
    public static bool SameLocation(Value a, Value b)
        => (a.Type is null || b.Type is null || a.Type.Equals(b.Type))
        && (a, b) switch
           {
               (SymbolValue symbolA, SymbolValue symbolB)
                   => symbolA.Symbol == symbolB.Symbol
                   && symbolA.Offset == symbolB.Offset,

               (RegisterValue registerA, RegisterValue registerB)
                   => registerA.RegisterManager == registerB.RegisterManager
                   && registerA.RegisterManager.GetRegisterIdFromAllocation(registerA)
                          == registerB.RegisterManager.GetRegisterIdFromAllocation(registerB),

               (IntegerValue integerA, IntegerValue integerB)
                   => integerA.Value == integerB.Value,

               _ => false
           };
}

/// <summary>
/// Represents a value with a type that doesn't depend on the context
/// </summary>
internal record StronglyTypedValue(TypeInfo Type)
    : Value(Type)
{
    public TypeInfo StrongType => Type!;
}

internal record SymbolValue(TypeInfo Type, string Symbol, int Offset)
    : StronglyTypedValue(Type)
{
    /// <summary>
    /// Represents the address written within the square brackets in the assembly code
    /// [Symbol + Offset]
    /// </summary>
    public string AddressString
    {
        get
        {
            string offsetAppendix = Offset switch
            {
                < 0 => $" - {-Offset}",
                > 0 => $" + {Offset}",
                _ => ""
            };

            return $"{Symbol}{offsetAppendix}";
        }
    }

    public override string ToString()
        => Type is FunctionPointerTypeInfo
               ? AddressString
               : $"{StrongType.AssemblyName} [{AddressString}]";
}

/// <param name="RegisterManager">The register manager managing the value</param>
/// <param name="AllocationId">The id of the allocation, not the register id</param>
internal record RegisterValue(TypeInfo Type, RegisterManager RegisterManager, int AllocationId)
    : StronglyTypedValue(Type)
{
    public string Name => RegisterManager.GetRegisterNameFromId(
        RegisterManager.GetRegisterIdFromAllocation(this),
        StrongType
    );

    public override string ToString() => Name;
}

internal record IntegerValue(TypeInfo? Type, long Value)
    : Value(Type)
{
    public override string ToString() => Value.ToString();
}
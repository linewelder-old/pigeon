﻿using System;
using System.Collections.Generic;
using CompilerLibrary.Compiling.Exceptions;
using CompilerLibrary.Parsing;

namespace CompilerLibrary.Compiling;

/// <summary>
/// Generates FASM code based on parsed Pigeon
/// </summary>
public class Compiler
{
    internal static readonly Dictionary<string, TypeInfo> COMPILED_TYPES = new()
    {
        { "i32", new TypeInfo(Size: 4, Name: "i32", IsSigned: true) },
        { "i16", new TypeInfo(Size: 2, Name: "i16", IsSigned: true) },
        { "i8",  new TypeInfo(Size: 1, Name: "i8",  IsSigned: true) },
        { "u32", new TypeInfo(Size: 4, Name: "u32", IsSigned: false) },
        { "u16", new TypeInfo(Size: 2, Name: "u16", IsSigned: false) },
        { "u8",  new TypeInfo(Size: 1, Name: "u8",  IsSigned: false) }
    };

    private const int ARGUMENT_OFFSET = 4;

    private readonly Dictionary<string, VariableInfo> variables = new();
    private readonly Dictionary<string, FunctionInfo> functions = new()
    {
        {
            "_read",
            new(
                SourceLocation: new("", 0, 0),
                AssemblySymbol: "_read",
                ReturnType: COMPILED_TYPES["i32"],
                Arguments: Array.Empty<FunctionArgument>(),
                Body: Array.Empty<SyntaxNode>()
            )
        },
        {
            "_write",
            new(
                SourceLocation: new("", 0, 0),
                AssemblySymbol: "_write",
                ReturnType: null,
                Arguments: new[]
                {
                    new FunctionArgument(
                        SourceLocation: new("", 0, 0),
                        Type: COMPILED_TYPES["i32"],
                        Name: "value"
                    )
                },
                Body: Array.Empty<SyntaxNode>()
            )
        },
    };

    private FunctionInfo currentFunction;
    private bool needsEndingLabel;

    private AssemblyGenerator assemblyGenerator;
    private RegisterManager registerManager;

    public Compiler()
    {
        currentFunction = null!;
        assemblyGenerator = null!;
        registerManager = null!;
    }

    /// <summary>
    /// Returns the TypeInfo for the given type
    /// </summary>
    public static TypeInfo GetTypeInfo(SyntaxNode type)
    {
        if (type is not IdentifierNode identifier)
        {
            throw new UnexpectedSyntaxNodeException(type, "type identifier");
        }

        if (COMPILED_TYPES.TryGetValue(identifier.Value, out TypeInfo? compiledType))
        {
            return compiledType;
        }

        throw new UnknownIdentifierException(identifier);
    }

    /// <summary>
    /// Adds a function to the declared functions list
    /// </summary>
    /// <param name="function">The function to register</param>
    public void RegisterFunction(FunctionDeclarationNode function)
    {
        FunctionArgument[] arguments = new FunctionArgument[function.Arguments.Length];
        for (int i = 0; i < function.Arguments.Length; i++)
        {
            FunctionArgumentDeclarationNode argument = function.Arguments[i];
            arguments[i] = new FunctionArgument(
                argument.Location,
                GetTypeInfo(argument.Type),
                argument.Identifier
            );
        }

        string assemblySymbol = AssemblyGenerator.GetAssemblySymbol(function.Identifier);

        functions.Add(assemblySymbol, new FunctionInfo(
            function.Location,
            assemblySymbol,
            function.ReturnType is null ? null : GetTypeInfo(function.ReturnType),
            arguments,
            function.Body
        ));
    }

    /// <summary>
    /// Adds a global variable to the declared variables list
    /// </summary>
    /// <param name="variable">The variable declaration</param>
    public void RegisterVariable(VariableDeclarationNode variable)
    {
        TypeInfo variableType = GetTypeInfo(variable.Type);
        if (variable.Value is not IntegerNode valueInteger)
        {
            throw new UnexpectedSyntaxNodeException(variable.Value, "a number");
        }

        long maximumValue = variableType.MaximumValue;
        if (valueInteger.Value > maximumValue)
        {
            throw new InvalidTypeCastException(
                variable.Value.Location,
                null, variableType,
                "possible value loss"
            );
        }

        string assemblySymbol = AssemblyGenerator.GetAssemblySymbol(variable.Identifier);
        string variableValue = valueInteger.Value.ToString();

        variables.Add(assemblySymbol, new VariableInfo(
            variable.Location,
            assemblySymbol,
            variableType,
            variableValue
        ));
    }

    /// <summary>
    /// Compiles the given declarations
    /// </summary>
    public void RegisterDeclarations(SyntaxNode[] nodes)
    {
        foreach (SyntaxNode node in nodes)
        {
            switch (node)
            {
                case VariableDeclarationNode variable:
                    RegisterVariable(variable);
                    break;

                case FunctionDeclarationNode function:
                    RegisterFunction(function);
                    break;

                default:
                    throw new UnexpectedSyntaxNodeException(
                        node, "variable or function declaration"
                    );
            }
        }
    }

    /// <summary>
    /// Converts an IntegerValue into another type
    /// </summary>
    /// <param name="node">The syntax node the conversion happens within,
    /// used for exception throwing</param>
    /// <param name="value">The value to convert</param>
    /// <param name="type">The type to convert into</param>
    /// <param name="explicitly"></param>
    /// <returns>The converted integer value</returns>
    private static IntegerValue ConvertIntegerValue(
        SyntaxNode node, IntegerValue value, TypeInfo type, bool explicitly = false
    )
    {
        if (value.Type is not null
         && value.Type.IsSigned != type.IsSigned
         && value.Value < 0)
        {
            throw new InvalidTypeCastException(
                node.Location,
                value.Type, type,
                "cannot change type's signedness"
            );
        }

        if (value.Value > type.MaximumValue || value.Value < type.MinimumValue)
        {
            if (!explicitly)
            {
                throw new InvalidTypeCastException(
                    node.Location,
                    value?.Type, type,
                    "possible value loss"
                );
            }

            long cutted = value.Value & type.Mask;
            long maxVal = type.MaximumValue;
            if (type.IsSigned && cutted > maxVal)
            {
                cutted = cutted - 2 * maxVal - 2;
            }

            return new IntegerValue(type, cutted);
        }

        return new IntegerValue(type, value.Value);
    }

    /// <summary>
    /// Generates data transfer from the source to the destination.
    /// Frees the source Value
    /// </summary>
    /// <param name="node">The syntax node the transfer happens within,
    /// used for exception throwing</param>
    /// <param name="destination">The location to put data to</param>
    /// <param name="source">The location to take data from</param>
    /// <param name="explicitTypeCast">If true, allows unsafe type casts</param>
    private void GenerateMov(
        SyntaxNode node, StronglyTypedValue destination,
        Value source, bool explicitTypeCast = false)
    {
        if (source.Type is not null && !explicitTypeCast)
        {
            if (destination.StrongType.IsSigned != source.Type.IsSigned)
            {
                throw new InvalidTypeCastException(
                    node.Location,
                    source.Type, destination.StrongType,
                    "the types must be either both signed or unsigned"
                );
            }

            if (destination.StrongType.Size < source.Type.Size)
            {
                throw new InvalidTypeCastException(
                    node.Location, source.Type, destination.StrongType,
                    "possible value loss"
                );
            }
        }

        if (Value.SameLocation(destination, source))
        {
            return;
        }

        // We cannot transfer data from a variable to another directly
        if (source is SymbolValue && destination is not RegisterValue)
        {
            RegisterValue transferRegister = registerManager.AllocateRegister(node, source.Type!);
            assemblyGenerator.EmitInstruction("mov", transferRegister, source);
            source = transferRegister;
        }

        if (source is IntegerValue integerValue)
        {
            assemblyGenerator.EmitInstruction(
                "mov", destination,
                ConvertIntegerValue(
                    node, integerValue, destination.StrongType, explicitTypeCast
                )
            );
        }
        else if (destination.StrongType.Size == source.Type!.Size)
        {
            assemblyGenerator.EmitInstruction("mov", destination, source);
        }
        else if (destination.StrongType.Size > source.Type.Size)
        {
            assemblyGenerator.EmitInstruction(
                destination.StrongType.IsSigned ? "movsx" : "movzx",
                destination, source
            );
        }
        else
        {
            Value converted = source switch
            {
                RegisterValue register => register with { Type = destination.StrongType },
                SymbolValue   symbol   => symbol   with { Type = destination.StrongType },
                _ => throw new ArgumentException("Unexpected value class", nameof(source)),
            };

            if (Value.SameLocation(destination, converted))
            {
                return;
            }

            assemblyGenerator.EmitInstruction("mov", destination, converted);
        }

        registerManager.FreeRegister(source);
    }

    /// <summary>
    /// Generates conversion of the value to the type
    /// </summary>
    /// <param name="node">The syntax node the conversion happens within,
    /// used for exception throwing</param>
    /// <param name="value">The value to convert</param>
    /// <param name="type">The type to convert to</param>
    /// <param name="explicitly">Whether the type cast is explicit</param>
    /// <returns>The location of the converted value</returns>
    private Value GenerateTypeCast(
        SyntaxNode node, Value value,
        TypeInfo type, bool explicitly = false)
    {
        RegisterValue CutRegister(RegisterValue register, TypeInfo type)
        {
            int registerId = registerManager.GetRegisterIdFromAllocation(register);
            string convertedRegister = RegisterManager.GetRegisterNameFromId(registerId, type);

            if (type.Size > register.Type!.Size)
            {
                if (type.IsSigned)
                {
                    assemblyGenerator.EmitInstruction(
                        "movsx", convertedRegister, register
                    );
                }
                else
                {
                    assemblyGenerator.EmitInstruction(
                        "and", convertedRegister, register.Type.Mask
                    );
                }
            }

            return register with { Type = type };
        }

        if (value.Type == type)
        {
            return value;
        }

        if (value is IntegerValue integerValue)
        {
            return ConvertIntegerValue(node, integerValue, type, explicitly);
        }

        if (!explicitly && value.Type!.IsSigned != type.IsSigned)
        {
            throw new InvalidTypeCastException(
                node.Location,
                value.Type, type,
                "cannot change type's signedness"
            );
        }

        if (value.Type!.Size > type.Size)
        {
            if (!explicitly)
            {
                throw new InvalidTypeCastException(
                    node.Location,
                    value.Type, type,
                    "possible value loss"
                );
            }

            return value switch
            {
                RegisterValue registerValue => CutRegister(registerValue, type),
                SymbolValue => value with { Type = type },
                _ => throw new ArgumentException("Unexpected value type"),
            };
        }

        switch (value)
        {
            case RegisterValue registerValue:
                return CutRegister(registerValue, type);

            default:
                RegisterValue conversionRegister = registerManager.AllocateRegister(node, type);
                assemblyGenerator.EmitInstruction(
                    type.IsSigned ? "movsx" : "movzx",
                    conversionRegister, value
                );

                return conversionRegister;
        }
    }

    /// <summary>
    /// Finds the variable of the given identifier
    /// </summary>
    /// <param name="identifier">The identifier of the variable to find</param>
    /// <returns>SymbolValue representing the found variable</returns>
    private SymbolValue FindSymbol(IdentifierNode identifier)
    {
        string symbol = AssemblyGenerator.GetAssemblySymbol(identifier.Value);
        int argumentIndex = Array.FindIndex(
            currentFunction.Arguments,
            x => x.Name == identifier.Value
        );

        if (argumentIndex >= 0)
        {
            // [esp - 12] = arg1        12 = (1 + 2) * ARGUMENT_OFFSET
            // [esp - 8]  = arg0        8  = (0 + 2) * ARGUMENT_OFFSET
            // [esp - 4]  = return eip
            // [esp]      = old ebp
            return new SymbolValue(
                currentFunction.Arguments[argumentIndex].Type,
                "ebp", (argumentIndex + 2) * ARGUMENT_OFFSET
            );
        }
        else if (variables.TryGetValue(symbol, out VariableInfo? variable))
        {
            return new SymbolValue(variable.Type, variable.AssemblySymbol, 0);
        }
        else if (functions.TryGetValue(symbol, out FunctionInfo? function))
        {
            return new SymbolValue(
                new FunctionPointerTypeInfo(function),
                function.AssemblySymbol, 0
            );
        }
        else
        {
            throw new UnknownIdentifierException(identifier);
        }
    }

    /// <summary>
    /// Evaluates the type of an expression
    /// </summary>
    /// <param name="node">The expression to evaluate type of</param>
    /// <returns>The evaluated type, if cannot be evaluated - null</returns>
    public TypeInfo? EvaluateType(SyntaxNode node)
    {
        switch (node)
        {
            case IdentifierNode identifier:
                return FindSymbol(identifier).Type;

            case IntegerNode:
                return null;

            case TypeCastNode typeCast:
                return GetTypeInfo(typeCast.Type);

            case NegationNode negation:
                TypeInfo? innerType = EvaluateType(negation.InnerExpression);
                if (!(innerType?.IsSigned ?? true))
                {
                    throw new UnsignedTypeException(
                        negation.Location, innerType,
                        "negation cannot be applied"
                    );
                }

                return innerType;

            case FunctionCallNode functionCall:
                TypeInfo? function = EvaluateType(functionCall.Function);
                if (function is not FunctionPointerTypeInfo functionType)
                {
                    throw new NotCallableTypeException(functionCall.Location, function);
                }

                return functionType.FunctionInfo.ReturnType;

            case BinaryNode binary:
                TypeInfo? leftType = EvaluateType(binary.Left);
                TypeInfo? rightType = EvaluateType(binary.Right);

                if (leftType is not null && rightType is not null
                 && leftType.IsSigned != rightType.IsSigned)
                {
                    throw new InvalidTypeCastException(
                        binary.Location,
                        rightType, leftType,
                        "operand types must be either both signed or unsigned"
                    );
                }

                return (leftType?.Size ?? 0) > (rightType?.Size ?? 0)
                    ? leftType : rightType;

            default:
                throw new UnexpectedSyntaxNodeException(node, "expression");
        }
    }

    /// <summary>
    /// Generates a function call
    /// </summary>
    /// <param name="mustReturnValue">Whether the function must return a value</param>
    /// <returns>The location of the returned value</returns>
    private Value? GenerateFunctionCall(FunctionCallNode functionCall, bool mustReturnValue)
    {
        Value function = CompileValue(functionCall.Function);
        if (function.Type is not FunctionPointerTypeInfo functionType)
        {
            throw new NotCallableTypeException(functionCall.Location, function.Type);
        }

        int stackSpaceAllocated = functionCall.Arguments.Length * ARGUMENT_OFFSET;
        if (stackSpaceAllocated > 0)
        {
            assemblyGenerator.EmitInstruction("sub", "esp", stackSpaceAllocated);
        }

        for (int i = 0; i < functionCall.Arguments.Length; i++)
        {
            SymbolValue argumentLocation = new(
                functionType.FunctionInfo.Arguments[i].Type,
                "esp", i * ARGUMENT_OFFSET
            );

            GenerateAssignment(
                functionCall,
                argumentLocation, functionCall.Arguments[i]
            );
        }

        RegisterValue? returnRegister = null;
        if (mustReturnValue)
        {
            if (functionType.FunctionInfo.ReturnType is null)
            {
                throw new NoReturnValueException(
                    functionCall.Location,
                    function.Type.Name
                );
            }

            // The register the function result is returned in
            (returnRegister, int oldValueNewRegister) = registerManager.RequireRegister(
                functionCall, functionType.FunctionInfo.ReturnType,
                RegisterManager.RETURN_REGISTER_ID
            );

            if (oldValueNewRegister >= 0)
            {
                assemblyGenerator.EmitInstruction(
                    "mov",
                    RegisterManager.GetRegisterNameFromId(
                        oldValueNewRegister, COMPILED_TYPES["i32"]),
                    RegisterManager.GetRegisterNameFromId(
                        RegisterManager.RETURN_REGISTER_ID, COMPILED_TYPES["i32"])
                );
            }
        }

        assemblyGenerator.EmitInstruction("call", function);
        if (stackSpaceAllocated > 0)
        {
            assemblyGenerator.EmitInstruction("add", "esp", stackSpaceAllocated);
        }

        return returnRegister;
    }

    /// <summary>
    /// Compiels an expression and appends the compiled assembly to the builder
    /// </summary>
    /// <param name="node">The expression to compile</param>
    /// <returns>Value representing the result of the expression</returns>
    private Value CompileValue(SyntaxNode node, TypeInfo? targetType = null)
    {
        switch (node)
        {
            case IdentifierNode identifier:
                return FindSymbol(identifier);

            case IntegerNode integer:
                if (targetType is not null
                 && (integer.Value > targetType.MaximumValue
                  || integer.Value < targetType.MinimumValue))
                {
                    throw new InvalidTypeCastException(
                        integer.Location,
                        null, targetType,
                        "possible value loss"
                    );
                }

                return new IntegerValue(targetType, integer.Value);

            case TypeCastNode typeCast:
                TypeInfo castInto = GetTypeInfo(typeCast.Type);
                return GenerateTypeCast(
                    typeCast,
                    CompileValue(typeCast.Value, castInto),
                    castInto, true
                );

            case NegationNode negation:
                Value inner = CompileValue(negation.InnerExpression, targetType);

                if (inner.Type is not null && !inner.Type.IsSigned)
                {
                    throw new UnsignedTypeException(
                        negation.Location, inner.Type,
                        "negation cannot be applied"
                    );
                }

                if (inner is not RegisterValue)
                {
                    RegisterValue resultRegister = registerManager.AllocateRegister(negation, inner.Type!);
                    GenerateMov(negation, resultRegister, inner);
                    inner = resultRegister;
                }

                assemblyGenerator.EmitInstruction("neg", inner);
                return inner;

            case FunctionCallNode functionCall:
                return GenerateFunctionCall(functionCall, true)!;

            case BinaryNode binary:
                TypeInfo? resultType = EvaluateType(binary) ?? targetType;

                Value left = CompileValue(binary.Left, resultType);
                Value right = CompileValue(binary.Right, resultType);

                if (left.Type is not null && right.Type is not null
                 && left.Type.IsSigned != right.Type.IsSigned)
                {
                    throw new InvalidTypeCastException(
                        binary.Location,
                        right.Type, left.Type,
                        "operand types must be either both signed or unsigned"
                    );
                }

                if (left is not RegisterValue)
                {
                    if (right is RegisterValue && binary.Operation is BinaryNodeOperation.Addition)
                    {
                        (left, right) = (right, left);
                    }
                    else
                    {
                        RegisterValue accumulator = registerManager.AllocateRegister(binary, resultType!);
                        GenerateMov(binary, accumulator, left);
                        left = accumulator;
                    }
                }

                if (resultType is not null)
                {
                    right = GenerateTypeCast(binary, right, resultType);
                }

                assemblyGenerator.EmitInstruction(
                    binary.Operation switch
                    {
                        BinaryNodeOperation.Addition    => "add",
                        BinaryNodeOperation.Subtraction => "sub",
                        _ => throw new NotImplementedException()
                    },
                    left, right
                );

                registerManager.FreeRegister(right);
                return left;

            default:
                throw new UnexpectedSyntaxNodeException(node, "expression");
        }
    }

    /// <summary>
    /// Compiles the expression, and movs it's result into destination
    /// </summary>
    /// <param name="node">The node of the assignment</param>
    /// <param name="destination">The location to mov the value into</param>
    /// <param name="expression">The expression to get value from</param>
    private void GenerateAssignment(SyntaxNode node, StronglyTypedValue destination, SyntaxNode expression)
    {
        expression = Optimizer.OptimizeExpression(expression);
        Value value;

        if (expression is TypeCastNode typeCast
         && GetTypeInfo(typeCast.Type) == destination.StrongType)
        {
            value = CompileValue(typeCast.Value);
            GenerateMov(node, destination, value, true);
        }
        else
        {
            value = CompileValue(expression, destination.StrongType);
            GenerateMov(node, destination, value);
        }
    }

    /// <summary>
    /// Compiles a statement and appends the compiled assembly to the builder
    /// </summary>
    /// <param name="node">The statement to compile</param>
    private void CompileStatement(SyntaxNode node)
    {
        switch (node)
        {
            case AssignmentNode assignment:
                Value left = CompileValue(assignment.Left);
                if (left is not SymbolValue leftSymbol)
                {
                    throw new NotLValueException(assignment.Left);
                }

                GenerateAssignment(assignment, leftSymbol, assignment.Right);
                break;

            case ReturnNode @return:
                if (@return.InnerExpression is null != currentFunction.ReturnType is null)
                {
                    throw new MismatchingReturnException(
                        @return.Location, currentFunction.ReturnType
                    );
                }

                if (@return.InnerExpression is not null)
                {
                    RegisterValue returnRegister
                        = registerManager.GetReturnRegister(currentFunction.ReturnType!);

                    GenerateAssignment(@return, returnRegister, @return.InnerExpression);
                    registerManager.FreeRegister(returnRegister);
                }

                if (@return != currentFunction.Body[^1])
                {
                    assemblyGenerator.EmitInstruction("jmp", $"end{currentFunction.AssemblySymbol}");
                    needsEndingLabel = true;
                }
                break;

            case FunctionCallNode functionCall:
                GenerateFunctionCall(functionCall, false);
                break;

            default:
                throw new UnexpectedSyntaxNodeException(node, "statement");
        }
    }

    /// <summary>
    /// Linkes all the compiled nodes and returns the generated FASM code
    /// </summary>
    /// <returns>The generated FASM code</returns>
    public string CompileAll()
    {
        assemblyGenerator = new AssemblyGenerator();
        registerManager = new RegisterManager();

        foreach (var pair in variables)
        {
            assemblyGenerator.EmitVariable(
                pair.Key, pair.Value.Type.AssemblyDeclaration,
                pair.Value.AssemblyValue
            );
        }

        foreach (var pair in functions)
        {
            if (pair.Key == "_read" || pair.Key == "_write")
            {
                continue;
            }

            currentFunction = pair.Value;
            needsEndingLabel = false;

            foreach (SyntaxNode node in pair.Value.Body)
            {
                CompileStatement(node);
            }

            // Beginning
            assemblyGenerator.EmitSymbol(pair.Key);
            assemblyGenerator.EmitInstructionToText("push", "ebp");
            assemblyGenerator.EmitInstructionToText("mov", "ebp", "esp");
            foreach (string register in registerManager.Used)
            {
                assemblyGenerator.EmitInstructionToText("push", register);
            }

            // Code
            assemblyGenerator.InsertFunctionCode();

            // Ending
            if (needsEndingLabel)
            {
                assemblyGenerator.EmitSymbol($"end{currentFunction.AssemblySymbol}");
            }
            foreach (string register in registerManager.Used)
            {
                assemblyGenerator.EmitInstructionToText("pop", register);
            }
            assemblyGenerator.EmitInstructionToText("leave");
            assemblyGenerator.EmitInstructionToText("ret");

            registerManager.ResetUsedRegisters();
        }
        
        

        assemblyGenerator.EmitInstruction("sub", "esp", "12");
        assemblyGenerator.EmitInstruction("lea", "eax", "[esp + 8]");
        assemblyGenerator.EmitInstruction("mov", "[esp + 4]", "eax");
        assemblyGenerator.EmitInstruction("mov", "dword [esp]", "scanf_format");
        assemblyGenerator.EmitInstruction("call", "[scanf]");
        assemblyGenerator.EmitInstruction("mov", "eax", "[esp + 8]");
        assemblyGenerator.EmitInstruction("add", "esp", "12");

        assemblyGenerator.EmitSymbol("_read");
        assemblyGenerator.EmitInstructionToText("push", "ebp");
        assemblyGenerator.EmitInstructionToText("mov", "ebp", "esp");
        assemblyGenerator.InsertFunctionCode();
        assemblyGenerator.EmitInstructionToText("leave");
        assemblyGenerator.EmitInstructionToText("ret");

        assemblyGenerator.EmitInstruction("sub", "esp", "8");
        assemblyGenerator.EmitInstruction("mov", "eax", "[ebp + 8]");
        assemblyGenerator.EmitInstruction("mov", "[esp + 4]", "eax");
        assemblyGenerator.EmitInstruction("mov", "dword [esp]", "printf_format");
        assemblyGenerator.EmitInstruction("call", "[printf]");
        assemblyGenerator.EmitInstruction("add", "esp", "8");

        assemblyGenerator.EmitSymbol("_write");
        assemblyGenerator.EmitInstructionToText("push", "ebp");
        assemblyGenerator.EmitInstructionToText("mov", "ebp", "esp");
        assemblyGenerator.InsertFunctionCode();
        assemblyGenerator.EmitInstructionToText("leave");
        assemblyGenerator.EmitInstructionToText("ret");

        return assemblyGenerator.LinkAssembly();
    }
}
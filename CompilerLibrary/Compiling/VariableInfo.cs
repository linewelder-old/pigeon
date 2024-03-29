﻿namespace CompilerLibrary.Compiling;

/// <summary>
/// Represents a compiled variable
/// </summary>
public record VariableInfo(
    Location SourceLocation,
    string AssemblySymbol,
    TypeInfo Type,
    string AssemblyValue
);

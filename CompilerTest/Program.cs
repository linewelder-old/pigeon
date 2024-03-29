﻿using System;
using System.IO;
using System.Text;
using CompilerLibrary;
using CompilerLibrary.Compiling;
using CompilerLibrary.Parsing;
using CompilerLibrary.Tokenizing;

const string code = @"
i32 a = 0;
i32 b = 0;

i32 sum(i32 a, i32 b)
{
    return a + b;
}

input()
{
    a = read();
    b = read();
}

calc()
{
    write(a + b);
    write(a - b);
}

i32 main()
{
    input();
    calc();

    return 0;
}
";

byte[] byteArray = Encoding.ASCII.GetBytes(code);
MemoryStream stream = new(byteArray);
Tokenizer tokenizer = new("<string>", new StreamReader(stream));
Parser parser = new(tokenizer);
Compiler compiler = new();

try
{
    SyntaxNode[] nodes = parser.ParseFile();

    compiler.RegisterDeclarations(nodes);
    Console.WriteLine(compiler.CompileAll());
}
catch (CompilerException ex)
{
    Console.WriteLine(ex.Message);
}
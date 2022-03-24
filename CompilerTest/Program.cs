﻿using System;
using System.IO;
using System.Text;
using CompilerLibrary;
using CompilerLibrary.Compiling;
using CompilerLibrary.Parsing;
using CompilerLibrary.Tokenizing;

const string code = @"
i32 input = 0;

i32 main()
{
    if (input == 2)
    {
        input = 1;
    }
    else if (input == 3)
    {
        input = 99;
    }
    else return 1;

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
    Debug.PrintSyntaxNode(nodes[1]);

    // compiler.RegisterDeclarations(nodes);
    // Console.WriteLine(compiler.CompileAll());
}
catch (CompilerException ex)
{
    Console.WriteLine(ex.Message);
}
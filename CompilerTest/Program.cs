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
    input = read();
    if (input == 1)
    {
        write(42);
    }

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
    do
    {
        tokenizer.NextToken();
        Console.WriteLine(tokenizer.CurrentToken);
    }
    while (!tokenizer.ReachedTheEOF);
    // SyntaxNode[] nodes = parser.ParseFile();
    // Debug.PrintSyntaxNode(nodes[0]);

    // compiler.RegisterDeclarations(nodes);
    // Console.WriteLine(compiler.CompileAll());
}
catch (CompilerException ex)
{
    Console.WriteLine(ex.Message);
}
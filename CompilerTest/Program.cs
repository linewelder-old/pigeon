﻿using System;
using System.IO;
using System.Text;
using CompilerLibrary;
using CompilerLibrary.Tokenizing;

namespace CompilerTest
{
    class Program
    {
        static void Main(string[] args)
        {
            string code = @"
i32 test = 29;
i32 anti_test = 20;
i32 anti_test2 = 43;
";

            byte[] byteArray = Encoding.ASCII.GetBytes(code);
            MemoryStream stream = new(byteArray);
            Tokenizer tokenizer = new("<string>", new StreamReader(stream));

            try
            {
                do Console.WriteLine(tokenizer.NextToken());
                while (!tokenizer.ReachedTheEOF);
            }
            catch (CompilerException ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }
}

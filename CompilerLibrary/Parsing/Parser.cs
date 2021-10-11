﻿using System.Collections.Generic;
using CompilerLibrary.Parsing.Exceptions;
using CompilerLibrary.Tokenizing;

namespace CompilerLibrary.Parsing
{
    /// <summary>
    /// Is used for syntax tree construction
    /// </summary>
    public class Parser
    {
        private readonly Dictionary<TokenType, BinaryNodeType> BINARY_OPERATIONS = new()
        {
            { TokenType.Plus, BinaryNodeType.Addition },
            { TokenType.Minus, BinaryNodeType.Subtraction },
            { TokenType.Star, BinaryNodeType.Multiplication },
            { TokenType.Slash, BinaryNodeType.Divizion }
        };

        private readonly Dictionary<BinaryNodeType, int> BINARY_OPERATION_PRIORITIES = new()
        {
            { BinaryNodeType.Addition, 0 },
            { BinaryNodeType.Subtraction, 0 },
            { BinaryNodeType.Multiplication, 1 },
            { BinaryNodeType.Divizion, 1 }
        };

        private readonly Tokenizer tokenizer;

        public Parser(Tokenizer tokenizer)
        {
            this.tokenizer = tokenizer;
            tokenizer.NextToken();
        }

        /// <summary>
        /// Checks if the current token is of the right type and advances
        /// </summary>
        /// <param name="type">The expected token type</param>
        /// <param name="expectation">What was expected</param>
        private void Consume(TokenType type, string expectation)
        {
            if (tokenizer.CurrentToken.Type != type)
                throw new UnexpectedTokenException(tokenizer.CurrentToken, expectation);

            tokenizer.NextToken();
        }

        /// <summary>
        /// Parses a type
        /// </summary>
        /// <returns>The parsed node</returns>
        private SyntaxNode ParseType()
        {
            Token token = tokenizer.CurrentToken;
            tokenizer.NextToken();

            return token switch
            {
                StringToken { Type: TokenType.Identifier } identifier =>
                    new IdentifierNode(identifier.Location, identifier.Value),

                _ => throw new UnexpectedTokenException(tokenizer.CurrentToken, "type")
            };
        }

        /// <summary>
        /// Parses an expression
        /// </summary>
        /// <returns>The parsed node</returns>
        private SyntaxNode ParseExpression()
        {
            SyntaxNode primaryExpression;

            Token firstToken = tokenizer.CurrentToken;
            tokenizer.NextToken();

            switch (firstToken)
            {
                case StringToken { Type: TokenType.Identifier } identifier:
                    primaryExpression = new IdentifierNode(identifier.Location, identifier.Value);
                    break;

                case IntegerToken { Type: TokenType.IntegerLiteral } integer:
                    primaryExpression = new IntegerNode(integer.Location, integer.Value);
                    break;

                case { Type: TokenType.LeftParenthesis }:
                    primaryExpression = ParseExpression();
                    Consume(TokenType.RightParenthesis, ")");
                    break;

                default:
                    throw new UnexpectedTokenException(firstToken, "expression");
            }

            if (BINARY_OPERATIONS.TryGetValue(tokenizer.CurrentToken.Type, out var operationType))
            {
                tokenizer.NextToken();
                bool rightExpressionIsInParentheses = tokenizer.CurrentToken.Type is TokenType.LeftParenthesis;

                SyntaxNode rightExpression = ParseExpression();

                // By default the expression will be returned right-to-left, but
                // if the right operation is not more prior than the current
                // then we need to make the right operation the parent one
                // op(a, op(b, c)) -> op(op(a, b), c)
                if (!rightExpressionIsInParentheses
                    && rightExpression is BinaryNode rightExpressionBinary
                    && BINARY_OPERATION_PRIORITIES[rightExpressionBinary.Type]
                           <= BINARY_OPERATION_PRIORITIES[operationType])
                {
                    return new BinaryNode(
                        primaryExpression.Location,
                        rightExpressionBinary.Type,
                        Left: new BinaryNode(
                            primaryExpression.Location,
                            operationType,
                            primaryExpression,
                            rightExpressionBinary.Left
                        ),
                        Right: rightExpressionBinary.Right
                    );
                }

                return new BinaryNode(
                    primaryExpression.Location,
                    operationType,
                    Left: primaryExpression,
                    Right: rightExpression
                );
            }

            // If there is no operator
            return primaryExpression;
        }

        /// <summary>
        /// Parses arguments for function declaration skipping the left parenthesis
        /// </summary>
        /// <returns>The parsed nodes</returns>
        private FunctionArgumentDeclarationNode[] ParseArgumentListDeclaration()
        {
            List<FunctionArgumentDeclarationNode> argumentList = new();

            if (tokenizer.CurrentToken.Type is TokenType.RightParenthesis)
                return argumentList.ToArray();

            while (true)
            {
                SyntaxNode argumentType = ParseType();
                if (tokenizer.CurrentToken is not StringToken { Type: TokenType.Identifier } name)
                    throw new UnexpectedTokenException(tokenizer.CurrentToken, "argument name");

                argumentList.Add(new FunctionArgumentDeclarationNode(
                    argumentType.Location,
                    argumentType,
                    name.Value
                ));

                tokenizer.NextToken();
                switch (tokenizer.CurrentToken.Type)
                {
                    case TokenType.RightParenthesis:
                        goto endOfArgumentList;

                    case not TokenType.Coma:
                        throw new UnexpectedTokenException(tokenizer.CurrentToken, ", or )");
                }

                tokenizer.NextToken();
            }

        endOfArgumentList:
            return argumentList.ToArray();
        }

        /// <summary>
        /// Parses a single declaration statement
        /// </summary>
        /// <returns>The parsed node</returns>
        public SyntaxNode Parse()
        {
            SyntaxNode type = ParseType();

            if (tokenizer.CurrentToken is not StringToken { Type: TokenType.Identifier } name)
                throw new UnexpectedTokenException(tokenizer.CurrentToken, "variable name");

            tokenizer.NextToken();
            Token currentToken = tokenizer.CurrentToken;

            tokenizer.NextToken();
            switch (currentToken.Type)
            {
                // It is a variable declaration
                case TokenType.Equals:
                    SyntaxNode value = ParseExpression();
                    Consume(TokenType.Semicolon, ";");

                    return new VariableDeclarationNode(
                        type.Location, type, name.Value, value
                    );

                // It is a function declaration
                case TokenType.LeftParenthesis:
                    FunctionArgumentDeclarationNode[] arguments = ParseArgumentListDeclaration();

                    return new FunctionDeclarationNode(
                        type.Location, type, name.Value,
                        arguments, null
                    );

                default:
                    throw new UnexpectedTokenException(currentToken, "= or (");
            }
        }
    }
}

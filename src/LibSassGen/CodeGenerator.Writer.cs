﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClangSharp;

namespace LibSassGen
{
    public partial class CodeGenerator
    {
        private readonly HashSet<string> handles = new HashSet<string>();

        private static readonly HashSet<string> PrivateFunctions = new HashSet<string>
        {
        };

        private CXTranslationUnit _currentTu;

        internal void ParseAndWrite()
        {
            var args = new[]
            {
                "-x",
                "c++",
                "-DWIN32",
                "-D_WIN32",
                "-DSASS2SCSS_H", // Don't include SASS2SCSS functions
                "-Wno-microsoft-enum-value",
                "-fparse-all-comments",
                "-fms-compatibility-version=19.00",
                "-I" + Path.Combine(Environment.CurrentDirectory, DefaultIncludeDir)

            };

            var files = new List<string>
            {
                Path.Combine(Environment.CurrentDirectory, Path.Combine(DefaultIncludeDir, "sass.h"))
            };

            // Global file
            _writer = _writerGlobal;
            WriteLine("// ----------------------------------------------------------");
            WriteLine("// This file was generated automatically from sass.h headers.");
            WriteLine("// DO NOT EDIT THIS FILE MANUALLY");
            WriteLine("// ----------------------------------------------------------");
            WriteLine("using System;");
            WriteLine("using System.Runtime.InteropServices;");
            WriteLine("using System.Text;");

            WriteLine();
            WriteLine("namespace SharpScss");
            WriteOpenBlock();

            // Functions
            _writer = _writerBody;
            WriteLine("internal static partial class LibSass");
            WriteOpenBlock();

            _currentTu = Parse(files, args);
            clang.visitChildren(clang.getTranslationUnitCursor(_currentTu), VisitCApi, new CXClientData(IntPtr.Zero));

            // Functions
            _writer = _writerBody;
            WriteCloseBlock();

            // Global file
            _writer = _writerGlobal;

            _writer.Write(_writerBody);

            WriteCloseBlock();
        }

        private static string GetCSharpName(string name)
        {
            return name;
        }

        private CppFunction GetCppFunction(CXType functionType, CXCursor cursor, string functionName)
        {
            var function = new CppFunction();
            var returnType = clang.getResultType(functionType);
            function.Name = functionName;
            function.Comment = clang.Cursor_getBriefCommentText(cursor).ToString();
            var returnParam = GetFunctionParamMarshalType(returnType);
            function.ReturnType = returnParam.Type;

            uint i = 0;
            clang.visitChildren(cursor, (cursorParam, cxCursor1, ptr) =>
            {
                if (cursorParam.kind == CXCursorKind.CXCursor_ParmDecl)
                {
                    var argName = clang.getCursorSpelling(cursorParam).ToString();

                    if (string.IsNullOrEmpty(argName))
                        argName = "param" + i;

                    var parameter = GetFunctionParamMarshalType(clang.getArgType(functionType, i));
                    parameter.Name = argName;
                    parameter.Type = parameter.Type.Replace("const ", string.Empty);

                    function.Parameters.Add(parameter);
                    i++;
                }
                return CXChildVisitResult.CXChildVisit_Continue;
            }, new CXClientData(IntPtr.Zero));
            return function;
        }

        private CppParameter GetFunctionParamMarshalType(CXType parameterType)
        {
            var param = new CppParameter();
            if (parameterType.kind == CXTypeKind.CXType_Unexposed)
            {
                parameterType = clang.getCanonicalType(parameterType);
            }

            var typeName = clang.getTypeSpelling(parameterType).ToString();
            if (typeName == "size_t")
            {
                param.Type = "size_t";
                return param;
            }

            if (parameterType.kind == CXTypeKind.CXType_Pointer)
            {
                var pointeeType = clang.getPointeeType(parameterType);
                if (pointeeType.kind == CXTypeKind.CXType_Unexposed)
                {
                    pointeeType = clang.getCanonicalType(pointeeType);
                }

                if (pointeeType.kind == CXTypeKind.CXType_Typedef)
                {
                    var finalPointeeType = clang.getCanonicalType(pointeeType);
                    if (finalPointeeType.kind == CXTypeKind.CXType_Record)
                    {
                        param.Type = GetCSharpName(clang.getTypeSpelling(pointeeType).ToString());
                        return param;
                    }
                    pointeeType = finalPointeeType;
                }

                if (pointeeType.kind == CXTypeKind.CXType_Record)
                {
                    param.Type = GetCSharpName(clang.getTypeSpelling(pointeeType).ToString());
                }
                else if (pointeeType.kind == CXTypeKind.CXType_Enum)
                {
                    param.Qualifier = "out";
                    param.Type = clang.getTypeSpelling(pointeeType).ToString();
                }
                else if (pointeeType.kind.IsChar())
                {
                    param.Type = "StringUtf8";
                }
                else
                {
                    param.Type = "IntPtr";
                }
                return param;
            }
            if (parameterType.kind == CXTypeKind.CXType_Typedef)
            {
                var finalPointeeType = clang.getCanonicalType(parameterType);

                bool hasPointer = false;
                while (finalPointeeType.kind == CXTypeKind.CXType_Pointer)
                {
                    finalPointeeType = clang.getPointeeType(finalPointeeType);
                    hasPointer = true;
                }

                if (finalPointeeType.kind == CXTypeKind.CXType_Record || hasPointer)
                {
                    param.Type = GetCSharpName(clang.getTypeSpelling(parameterType).ToString());
                    return param;
                }
                parameterType = clang.getCanonicalType(parameterType);
            }
            else if (parameterType.kind == CXTypeKind.CXType_Enum)
            {
                param.Type = GetCSharpName(clang.getTypeSpelling(parameterType).ToString());
                return param;
            }

            param.Type = parameterType.kind.ToPrimitiveCsType();
            return param;
        }

        private CXTranslationUnit Parse(List<string> files, string[] args)
        {
            var tmpfile = Path.GetTempFileName();

            var lines = new List<string>();
            lines.AddRange(files.Select(name => "#include \"" + name + "\""));
            File.WriteAllLines(tmpfile, lines);

            CXTranslationUnit translationUnit;

            CXUnsavedFile unsavedFile;
            var clang_create_index = clang.createIndex(0, 0);
            var translationUnitError = clang.parseTranslationUnit2(clang_create_index, tmpfile, args, args.Length,
                out unsavedFile, 0, (uint) CXTranslationUnit_Flags.CXTranslationUnit_SkipFunctionBodies,
                out translationUnit);

            //if (translationUnitError != CXErrorCode.CXError_Success)
            if (translationUnitError != CXErrorCode.CXError_Success)
                WriteLine("#error: " + translationUnitError);
            var numDiagnostics = clang.getNumDiagnostics(translationUnit);

            var defaultDiag = clang.defaultDiagnosticDisplayOptions();
            for (uint i = 0; i < numDiagnostics; ++i)
            {
                var diagnostic = clang.getDiagnostic(translationUnit, i);
                var text = clang.formatDiagnostic(diagnostic, defaultDiag);
                WriteLine("// " + text);
                clang.disposeDiagnostic(diagnostic);
            }
            return translationUnit;
        }

        private CXChildVisitResult VisitCApi(CXCursor cursor, CXCursor parent, IntPtr data)
        {
            if (cursor.IsInSystemHeader())
                return CXChildVisitResult.CXChildVisit_Continue;

            var kind = clang.getCursorKind(cursor);
            switch (kind)
            {
                case CXCursorKind.CXCursor_EnumDecl:
                    var enumName = GetTypeAsString(clang.getCursorType(cursor));
                    var baseType = clang.getEnumDeclIntegerType(cursor).kind.ToPrimitiveCsType() ?? "int";

                    var enums = new List<KeyValuePair<string, long>>();
                    clang.visitChildren(cursor, (cxCursor, parent1, clientData) =>
                    {
                        var valName = clang.getCursorSpelling(cxCursor).ToString();
                        var valValue = clang.getEnumConstantDeclValue(cxCursor);
                        enums.Add(new KeyValuePair<string, long>(valName, valValue));
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }, new CXClientData(IntPtr.Zero));

                    WriteLine();

                    var enumcs = GetCSharpName(enumName);
                    WriteLine("public enum " + enumcs + (baseType != "int" ? " : " + baseType : string.Empty));
                    WriteOpenBlock();
                    foreach (var enumItem in enums)
                        WriteLine(enumItem.Key + " = " + enumItem.Value + ",");
                    WriteCloseBlock();

                    return CXChildVisitResult.CXChildVisit_Continue;

                case CXCursorKind.CXCursor_StructDecl:
                case CXCursorKind.CXCursor_UnionDecl:
                {
                        AddOpaqueStruct(cursor);

                    // TODO: Check that a struct is either an opaque pointer (used only through a pointer)
                    // or a real struct that we should generate fields for.

                    return CXChildVisitResult.CXChildVisit_Continue;
                }
                case CXCursorKind.CXCursor_TypedefDecl:
                {
                    var canonicalType = clang.getCanonicalType(clang.getTypedefDeclUnderlyingType(cursor));

                    bool hasPointer = false;
                    while (canonicalType.kind == CXTypeKind.CXType_Pointer)
                    {
                        canonicalType = clang.getPointeeType(canonicalType);
                        hasPointer = true;
                    }

                    if (canonicalType.kind == CXTypeKind.CXType_Record || hasPointer)
                    {
                        AddOpaqueStruct(cursor);
                        // TODO: Check that a struct is either an opaque pointer (used only through a pointer)
                        // or a real struct that we should generate fields for.
                    }

                    return CXChildVisitResult.CXChildVisit_Continue;
                }

                case CXCursorKind.CXCursor_FunctionDecl:
                    _writer = _writerBody;
                    var functionName = clang.getCursorSpelling(cursor).ToString();

                    var functionType = clang.getCursorType(cursor);
                    WriteLine();

                    WriteFunction(functionType, cursor, functionName);

                    return CXChildVisitResult.CXChildVisit_Continue;
            }

            return CXChildVisitResult.CXChildVisit_Recurse;
        }

        private void AddOpaqueStruct(CXCursor cursor)
        {
            var structName = GetTypeAsString(clang.getCursorType(cursor));
            var csharpStructName = GetCSharpName(structName);

            // Keep a list of handle types
            if (!handles.Add(csharpStructName))
            {
                return;
            }

            // We assume that all structs are just opaque pointers
            WriteLine();

            WriteLine("public partial struct " + csharpStructName);
            WriteOpenBlock();

            WriteLine($"public {csharpStructName}(IntPtr pointer)");
            WriteOpenBlock();
            WriteLine("Pointer = pointer;");
            WriteCloseBlock();

            WriteLine("public IntPtr Pointer { get; }");
            WriteCloseBlock();
        }

        private void WriteFunction(CXType functionType, CXCursor cursor, string functionName)
        {
            var function = GetCppFunction(functionType, cursor, functionName);
            // Writes the function DllImport
            WriteFunction(function);
        }

        private void WriteFunction(CppFunction function)
        {
            var signature = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(function.Comment))
                WriteComment(function.Comment);

            var isPrivate = PrivateFunctions.Contains(function.Name);

            signature.Append((isPrivate ? "private " : "public ") + "static extern ");

            // Function return type
            signature.Append(function.ReturnType);

            signature.Append(" ");

            var functionName = function.Name + (isPrivate ? "Internal" : string.Empty);

            // Function name
            signature.Append(functionName);

            signature.Append("(");
            for (var j = 0; j < function.Parameters.Count; j++)
            {
                var parameter = function.Parameters[j];
                if (j > 0)
                    signature.Append(", ");
                if (parameter.Attributes != null)
                    signature.Append(parameter.Attributes).Append(" ");
                //if (!marshal && j == 0 && handles.Contains(parameter.Type))
                //    signature.Append("this ");
                if (parameter.Qualifier != null)
                    signature.Append(parameter.Qualifier).Append(" ");
                signature.Append(parameter.Type);
                signature.Append(" ");
                signature.Append(parameter.Name);
            }
            signature.Append(")");

            WriteLine(
                $"[DllImport(LibSassDll, EntryPoint = \"{function.Name}\",CallingConvention = CallingConvention.Cdecl)]");
            WriteLine(signature + ";");
        }

        private class CppFunction
        {
            public readonly List<CppParameter> Parameters;

            public string Comment;

            public string Name;

            public string ReturnType;

            public CppFunction()
            {
                Parameters = new List<CppParameter>();
            }
        }

        private struct CppParameter
        {
            public string Name;

            public string Attributes;

            public string Qualifier;

            public string Type;
        }
    }
}

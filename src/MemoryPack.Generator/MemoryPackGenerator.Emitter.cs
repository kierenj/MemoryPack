﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;
using System.Text;

namespace MemoryPack.Generator;

partial class MemoryPackGenerator
{
    static void Generate(TypeDeclarationSyntax syntax, Compilation compilation, string? serializationInfoLogDirectoryPath, in SourceProductionContext context)
    {
        var semanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);

        var typeSymbol = semanticModel.GetDeclaredSymbol(syntax, context.CancellationToken);
        if (typeSymbol == null)
        {
            return;
        }

        // verify is partial
        if (!IsPartial(syntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MustBePartial, syntax.Identifier.GetLocation(), typeSymbol.Name));
            return;
        }

        // nested is not allowed
        if (IsNested(syntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NestedNotAllow, syntax.Identifier.GetLocation(), typeSymbol.Name));
            return;
        }

        var reference = new ReferenceSymbols(compilation);
        var typeMeta = new TypeMeta(typeSymbol, reference);

        if (typeMeta.GenerateType == GenerateType.NoGenerate)
        {
            return;
        }

        // ReportDiagnostic when validate failed.
        if (!typeMeta.Validate(syntax, context))
        {
            return;
        }

        var fullType = typeMeta.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "")
            .Replace("<", "_")
            .Replace(">", "_");

        var sw = new StringWriter();

        sw.WriteLine(@"
// <auto-generated/>
#nullable enable
#pragma warning disable CS0162 // Unreachable code
#pragma warning disable CS0164 // This label has not been referenced
#pragma warning disable CS0219 // Variable assigned but never used
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8601 // Possible null reference assignment
#pragma warning disable CS8604 // Possible null reference argument for parameter
#pragma warning disable CS8631 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match constraint type.

using System;
using MemoryPack;
");

        var ns = typeMeta.Symbol.ContainingNamespace;
        if (!ns.IsGlobalNamespace)
        {
            sw.WriteLine($"namespace {ns};");
        }
        sw.WriteLine();

        // Write document comment as remarks
        if (typeMeta.GenerateType == GenerateType.Object)
        {
            BuildDebugInfo(sw, typeMeta, true);

            // also output to log
            if (serializationInfoLogDirectoryPath != null)
            {
                try
                {
                    if (!Directory.Exists(serializationInfoLogDirectoryPath))
                    {
                        Directory.CreateDirectory(serializationInfoLogDirectoryPath);
                    }
                    var logSw = new StringWriter();
                    BuildDebugInfo(logSw, typeMeta, false);
                    var message = logSw.ToString();

                    File.WriteAllText(Path.Combine(serializationInfoLogDirectoryPath, $"{fullType}.txt"), message, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex.ToString());
                }
            }
        }

        // emit type info
        typeMeta.Emit(sw);

        var code = sw.ToString();

        context.AddSource($"{fullType}.MemoryPackFormatter.g.cs", code);
    }

    static bool IsPartial(TypeDeclarationSyntax typeDeclaration)
    {
        return typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
    }

    static bool IsNested(TypeDeclarationSyntax typeDeclaration)
    {
        return typeDeclaration.Parent is TypeDeclarationSyntax;
    }

    static void BuildDebugInfo(StringWriter sw, TypeMeta type, bool xmlDocument)
    {
        string WithEscape(ISymbol symbol)
        {
            var str = symbol.FullyQualifiedToString().Replace("global::", "");
            if (xmlDocument)
            {
                return str.Replace("<", "&lt;").Replace(">", "&gt;");
            }
            else
            {
                return str;
            }
        }

        if (!xmlDocument)
        {
            sw.WriteLine(WithEscape(type.Symbol));
            sw.WriteLine("---");
        }
        else
        {
            sw.WriteLine("/// <remarks>");
            sw.WriteLine("/// MemoryPack serialize members:<br/>");
            sw.WriteLine("/// <code>");
        }

        foreach (var item in type.Members)
        {
            if (xmlDocument)
            {
                sw.Write("/// <b>");
            }

            if (type.IsUnmanagedType)
            {
                sw.Write("unmanaged ");
            }

            sw.Write(WithEscape(item.MemberType));
            if (xmlDocument)
            {
                sw.Write("</b>");
            }

            sw.Write(" ");
            sw.Write(item.Name);

            if (xmlDocument)
            {
                sw.WriteLine("<br/>");
            }
            else
            {
                sw.WriteLine();
            }
        }
        if (xmlDocument)
        {
            sw.WriteLine("/// </code>");
            sw.WriteLine("/// </remarks>");
        }
    }
}

public partial class TypeMeta
{
    public void Emit(StringWriter writer)
    {
        if (IsUnion)
        {
            writer.WriteLine(EmitUnionTemplate());
            return;
        }

        if (GenerateType == GenerateType.Collection)
        {
            writer.WriteLine(EmitGenericCollectionTemplate());
            return;
        }

        var serializeBody = "";
        var deserializeBody = "";
        if (IsUnmanagedType)
        {
            serializeBody = $$"""
        writer.WriteUnmanaged(value);
""";
            deserializeBody = $$"""
        reader.ReadUnmanaged(out value);
""";
        }
        else
        {
            serializeBody = EmitSerializeBody();
            deserializeBody = EmitDeserializeBody();
        }

        var classOrStructOrRecord = (IsRecord, IsValueType) switch
        {
            (true, true) => "record struct",
            (true, false) => "record",
            (false, true) => "struct",
            (false, false) => "class",
        };

        var nullable = IsValueType ? "" : "?";

        var code = $$"""
partial {{classOrStructOrRecord}} {{TypeName}} : IMemoryPackable<{{TypeName}}>
{
    static {{Symbol.Name}}()
    {
        MemoryPackFormatterProvider.Register<{{TypeName}}>();
    }

    static void IMemoryPackFormatterRegister.RegisterFormatter()
    {
        if (!MemoryPackFormatterProvider.IsRegistered<{{TypeName}}>())
        {
            MemoryPackFormatterProvider.Register(new MemoryPack.Formatters.MemoryPackableFormatter<{{TypeName}}>());
        }
{{EmitAdditionalRegisterFormatter("        ")}}
    }

    static void IMemoryPackable<{{TypeName}}>.Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref {{TypeName}}{{nullable}} value)
    {
{{OnSerializing.Select(x => "        " + x.Emit()).NewLine()}}
{{serializeBody}}
    END:
{{OnSerialized.Select(x => "        " + x.Emit()).NewLine()}}
        return;
    }

    static void IMemoryPackable<{{TypeName}}>.Deserialize(ref MemoryPackReader reader, scoped ref {{TypeName}}{{nullable}} value)
    {
{{OnDeserializing.Select(x => "        " + x.Emit()).NewLine()}}
{{deserializeBody}}
    END:
{{OnDeserialized.Select(x => "        " + x.Emit()).NewLine()}}
        return;
    }
}
""";

        writer.WriteLine(code);
    }

    private string EmitDeserializeBody()
    {
        var count = Members.Length;

        return $$"""
        if (!reader.TryReadObjectHeader(out var count))
        {
            value = default!;
            goto END;
        }
        
{{Members.Select(x => $"        {x.MemberType.FullyQualifiedToString()} __{x.Name};").NewLine()}}

        if (count == {{count}})
        {
            {{(IsValueType ? "" : "if (value == null)")}}
            {
{{EmitDeserializeMembers(Members, "                ")}}

                goto NEW;
            }
{{(IsValueType ? "#if false" : "            else")}}
            {
{{Members.Select(x => $"                __{x.Name} = value.{x.Name};").NewLine()}}

{{Members.Select(x => "                " + x.EmitReadRefDeserialize()).NewLine()}}

                goto SET;
            }
{{(IsValueType ? "#endif" : "")}}
        }
        else if (count > {{count}})
        {
            MemoryPackSerializationException.ThrowInvalidPropertyCount({{count}}, count);
            goto END;
        }
        else
        {
            {{(IsValueType ? "" : "if (value == null)")}}
            {
{{Members.Select(x => $"               __{x.Name} = default!;").NewLine()}}
            }
{{(IsValueType ? "#if false" : "            else")}}
            {
{{Members.Select(x => $"               __{x.Name} = value.{x.Name};").NewLine()}}
            }
{{(IsValueType ? "#endif" : "")}}

            if (count == 0) goto SKIP_READ;
{{Members.Select((x, i) => "            " + x.EmitReadRefDeserialize() + $" if (count == {i + 1}) goto SKIP_READ;").NewLine()}}

    SKIP_READ:
            {{(IsValueType ? "" : "if (value == null)")}}
            {
                goto NEW;
            }
{{(IsValueType ? "#if false" : "            else")}}            
            {
                goto SET;
            }
{{(IsValueType ? "#endif" : "")}}
        }

    SET:
        {{(!IsUseEmptyConstructor ? "goto NEW;" : "")}}
{{Members.Where(x => x.IsAssignable).Select(x => $"        {(IsUseEmptyConstructor ? "" : "// ")}value.{x.Name} = __{x.Name};").NewLine()}}
        goto END;

    NEW:
        value = {{EmitConstructor()}}
        {
{{EmitDeserializeConstruction("            ")}}
        };
""";
    }

    string EmitAdditionalRegisterFormatter(string indent)
    {
        // NOTE: analyze and add more formatters
        var enums = Members.Where(x => x.Kind == MemberKind.Enum)
            .Select(x => x.MemberType.FullyQualifiedToString())
            .Distinct()
            .ToArray();

        if (enums.Length == 0) return "";

        var sb = new StringBuilder();
        foreach (var item in enums)
        {
            sb.AppendLine($"{indent}if (!MemoryPackFormatterProvider.IsRegistered<{item}>())");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    MemoryPackFormatterProvider.Register(new MemoryPack.Formatters.UnmanagedFormatter<{item}>());");
            sb.AppendLine($"{indent}}}");
        }

        return sb.ToString();
    }

    string EmitSerializeBody()
    {
        return $$"""
{{(!IsValueType ? $$"""
        if (value == null)
        {
            writer.WriteNullObjectHeader();
            goto END;
        }
""" : "")}}

{{EmitSerializeMembers(Members, "        ")}}
""";
    }

    public string EmitSerializeMembers(MemberMeta[] members, string indent)
    {
        // members is guranteed writable.
        if (members.Length == 0)
        {
            return $"{indent}writer.WriteObjectHeader(0);";
        }

        var sb = new StringBuilder();
        for (int i = 0; i < members.Length; i++)
        {
            if (members[i].Kind != MemberKind.Unmanaged)
            {
                sb.Append(indent);
                if (i == 0)
                {
                    sb.AppendLine($"writer.WriteObjectHeader({Members.Length});");
                    sb.Append(indent);
                }

                sb.AppendLine(members[i].EmitSerialize());
                continue;
            }

            // search optimization
            var optimizeFrom = i;
            var optimizeTo = i;
            var limit = Math.Min(members.Length, i + 15);
            for (int j = i; j < limit; j++)
            {
                if (members[j].Kind == MemberKind.Unmanaged)
                {
                    optimizeTo = j;
                    continue;
                }
                else
                {
                    break;
                }
            }

            // write method
            sb.Append(indent);
            if (optimizeFrom == 0)
            {
                sb.Append("writer.WriteUnmanagedWithObjectHeader(");
                sb.Append(members.Length);
                sb.Append(", ");
            }
            else
            {
                sb.Append("writer.WriteUnmanaged(");
            }

            for (int index = optimizeFrom; index <= optimizeTo; index++)
            {
                if (index != i)
                {
                    sb.Append(", ");
                }
                sb.Append("value.");
                sb.Append(members[index].Name);
            }
            sb.AppendLine(");");

            i = optimizeTo;
        }

        return sb.ToString();
    }

    // for optimize, can use same count, value == null.
    public string EmitDeserializeMembers(MemberMeta[] members, string indent)
    {
        // {{Members.Select(x => "                " + x.EmitReadToDeserialize()).NewLine()}}
        var sb = new StringBuilder();
        for (int i = 0; i < members.Length; i++)
        {
            if (members[i].Kind != MemberKind.Unmanaged)
            {
                sb.Append(indent);
                sb.AppendLine(members[i].EmitReadToDeserialize());
                continue;
            }

            // search optimization
            var optimizeFrom = i;
            var optimizeTo = i;
            var limit = Math.Min(members.Length, i + 15);
            for (int j = i; j < limit; j++)
            {
                if (members[j].Kind == MemberKind.Unmanaged)
                {
                    optimizeTo = j;
                    continue;
                }
                else
                {
                    break;
                }
            }

            // write read method
            sb.Append(indent);
            sb.Append("reader.ReadUnmanaged(");

            for (int index = optimizeFrom; index <= optimizeTo; index++)
            {
                if (index != i)
                {
                    sb.Append(", ");
                }
                sb.Append("out __");
                sb.Append(members[index].Name);
            }
            sb.AppendLine(");");

            i = optimizeTo;
        }

        return sb.ToString();
    }

    string EmitConstructor()
    {
        // noee need `;` because after using object initializer
        if (this.Constructor == null || this.Constructor.Parameters.Length == 0)
        {
            return $"new {TypeName}()";
        }
        else
        {
            var nameDict = Members.ToDictionary(x => x.Name, x => x.Name, StringComparer.OrdinalIgnoreCase);
            var parameters = this.Constructor.Parameters
                .Select(x =>
                {
                    if (nameDict.TryGetValue(x.Name, out var name))
                    {
                        return $"__{name}";
                    }
                    return null; // invalid, validated.
                })
                .Where(x => x != null);

            return $"new {TypeName}({string.Join(", ", parameters)})";
        }
    }

    string EmitDeserializeConstruction(string indent)
    {
        // all value is deserialized, __Name is exsits.
        return string.Join("," + Environment.NewLine, Members
            .Where(x => x.IsSettable && !x.IsConstructorParameter)
            .Select(x => $"{indent}{x.Name} = __{x.Name}"));
    }

    string EmitUnionTemplate()
    {
        var classOrInterface = Symbol.TypeKind == TypeKind.Interface ? "interface" : "class";

        var code = $$"""

partial {{classOrInterface}} {{TypeName}} : IMemoryPackFormatterRegister
{
    static {{Symbol.Name}}()
    {
        MemoryPackFormatterProvider.Register<{{TypeName}}>();
    }

    static void IMemoryPackFormatterRegister.RegisterFormatter()
    {
        if (!MemoryPackFormatterProvider.IsRegistered<{{TypeName}}>())
        {
            MemoryPackFormatterProvider.Register(new {{Symbol.Name}}Formatter());
        }
    }

    sealed class {{Symbol.Name}}Formatter : MemoryPackFormatter<{{TypeName}}>
    {
{{EmitUnionTypeToTagField()}}

        public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref {{TypeName}}? value)
        {
{{OnSerializing.Select(x => "            " + x.Emit()).NewLine()}}
{{EmitUnionSerializeBody()}}
{{OnSerialized.Select(x => "            " + x.Emit()).NewLine()}}
        }

        public override void Deserialize(ref MemoryPackReader reader, scoped ref {{TypeName}}? value)
        {
{{OnDeserializing.Select(x => "            " + x.Emit()).NewLine()}}
{{EmitUnionDeserializeBody()}}
{{OnDeserialized.Select(x => "            " + x.Emit()).NewLine()}}            
        }
    }
}
""";

        return code;
    }

    string ToUnionTagTypeFullyQualifiedToString(INamedTypeSymbol type)
    {
        if (type.IsGenericType && this.Symbol.IsGenericType)
        {
            // when generic type, it is unconstructed.( typeof(T<>) ) so construct symbol's T
            var typeName = string.Join(", ", this.Symbol.TypeArguments.Select(x => x.FullyQualifiedToString()));
            return type.FullyQualifiedToString().Replace("<>", "<" + typeName + ">");
        }
        else
        {
            return type.FullyQualifiedToString();
        }
    }

    string EmitUnionTypeToTagField()
    {
        var elements = UnionTags.Select(x => $"            {{ typeof({ToUnionTagTypeFullyQualifiedToString(x.Type)}), {x.Tag} }},").NewLine();

        return $$"""
        static readonly System.Collections.Generic.Dictionary<Type, byte> __typeToTag = new({{UnionTags.Length}})
        {
{{elements}}
        };
""";
    }

    string EmitUnionSerializeBody()
    {
        var writeBody = UnionTags
            .Select(x =>
            {
                var method = x.Type.IsWillImplementIMemoryPackable(reference)
                    ? "WritePackable"
                    : "WriteObject";
                return $"                    case {x.Tag}: writer.{method}(System.Runtime.CompilerServices.Unsafe.As<{TypeName}?, {ToUnionTagTypeFullyQualifiedToString(x.Type)}>(ref value)); break;";
            })
            .NewLine();

        return $$"""
            if (value == null)
            {
                writer.WriteNullObjectHeader();
{{OnSerialized.Select(x => "            " + x.Emit()).NewLine()}}
                return;
            }

            if (__typeToTag.TryGetValue(value.GetType(), out var tag))
            {
                writer.WriteUnionHeader(tag);

                switch (tag)
                {
{{writeBody}}                
                    default:
                        break;
                }
            }
            else
            {
                MemoryPackSerializationException.ThrowNotFoundInUnionType(value.GetType(), typeof({{TypeName}}));
            }
""";
    }

    string EmitUnionDeserializeBody()
    {
        var readBody = UnionTags.Select(x =>
        {
            var method = x.Type.IsWillImplementIMemoryPackable(reference)
                ? "ReadPackable"
                : "ReadObject";
            return $$"""
                case {{x.Tag}}:
                    if (value is {{ToUnionTagTypeFullyQualifiedToString(x.Type)}})
                    {
                        reader.{{method}}(ref System.Runtime.CompilerServices.Unsafe.As<{{TypeName}}?, {{ToUnionTagTypeFullyQualifiedToString(x.Type)}}>(ref value));
                    }
                    else
                    {
                        value = reader.{{method}}<{{ToUnionTagTypeFullyQualifiedToString(x.Type)}}>();
                    }
                    break;
""";
        }).NewLine();


        return $$"""
            if (!reader.TryReadUnionHeader(out var tag))
            {
                value = default;
{{OnDeserialized.Select(x => "                " + x.Emit()).NewLine()}}
                return;
            }
        
            switch (tag)
            {
{{readBody}}
                default:
                    MemoryPackSerializationException.ThrowInvalidTag(tag, typeof({{TypeName}}));
                    break;
            }
""";
    }

    string EmitGenericCollectionTemplate()
    {
        var (collectionKind, collectionSymbol) = ParseCollectionKind();
        var methodName = collectionKind switch
        {
            CollectionKind.Collection => "Collection",
            CollectionKind.Set => "Set",
            CollectionKind.Dictionary => "Dictionary",
            _ => "",
        };

        var typeArgs = string.Join(", ", collectionSymbol!.TypeArguments.Select(x => x.FullyQualifiedToString()));

        var code = $$"""
partial class {{TypeName}} : IMemoryPackFormatterRegister
{
    static {{Symbol.Name}}()
    {
        MemoryPackFormatterProvider.Register<{{TypeName}}>();
    }

    static void IMemoryPackFormatterRegister.RegisterFormatter()
    {
        if (!MemoryPackFormatterProvider.IsRegistered<{{TypeName}}>())
        {
            MemoryPackFormatterProvider.Register{{methodName}}<{{TypeName}}, {{typeArgs}}>();
        }
    }
}
""";

        return code;
    }
}

public partial class MethodMeta
{
    public string Emit()
    {
        if (IsStatic)
        {
            return $"{Name}();";
        }
        else
        {
            if (IsValueType)
            {
                return $"value.{Name}();";
            }
            else
            {
                return $"value?.{Name}();";
            }
        }
    }
}

public partial class MemberMeta
{
    public string EmitSerialize()
    {
        switch (Kind)
        {
            case MemberKind.MemoryPackable:
                return $"writer.WritePackable(value.{Name});";
            case MemberKind.Unmanaged:
                return $"writer.WriteUnmanaged(value.{Name});";
            case MemberKind.String:
                return $"writer.WriteString(value.{Name});";
            case MemberKind.UnmanagedArray:
                return $"writer.WriteUnmanagedArray(value.{Name});";
            case MemberKind.Array:
                return $"writer.WritedArray(value.{Name});";
            default:
                return $"writer.WriteObject(value.{Name});";
        }
    }

    public string EmitReadToDeserialize()
    {
        switch (Kind)
        {
            case MemberKind.MemoryPackable:
                return $"__{Name} = reader.ReadPackable<{MemberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>();";
            case MemberKind.Unmanaged:
                return $"reader.ReadUnmanaged(out __{Name});";
            case MemberKind.String:
                return $"__{Name} = reader.ReadString();";
            case MemberKind.UnmanagedArray:
                return $"__{Name} = reader.ReadUnmanagedArray<{(MemberType as IArrayTypeSymbol)!.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>();";
            case MemberKind.Array:
                return $"__{Name} = reader.ReadArray<{(MemberType as IArrayTypeSymbol)!.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>();";
            default:
                return $"__{Name} = reader.ReadObject<{MemberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>();";
        }
    }

    public string EmitReadRefDeserialize()
    {
        switch (Kind)
        {
            case MemberKind.MemoryPackable:
                return $"reader.ReadPackable(ref __{Name});";
            case MemberKind.Unmanaged:
                return $"reader.ReadUnmanaged(out __{Name});";
            case MemberKind.String:
                return $"__{Name} = reader.ReadString();";
            case MemberKind.UnmanagedArray:
                return $"reader.ReadUnmanagedArray(ref __{Name});";
            case MemberKind.Array:
                return $"reader.ReadArray(ref __{Name});";
            default:
                return $"reader.ReadObject(ref __{Name});";
        }
    }
}


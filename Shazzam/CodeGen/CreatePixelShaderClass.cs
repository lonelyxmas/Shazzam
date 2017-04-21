﻿namespace Shazzam.CodeGen
{
    using System;
    using System.CodeDom;
    using System.CodeDom.Compiler;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using System.Windows;
    using System.Windows.Media;
    using System.Windows.Media.Media3D;
    using Microsoft.CSharp;
    using Shazzam.Converters;
    using Shazzam.Properties;

    internal static class CreatePixelShaderClass
    {
        public static string GetSourceText(CodeDomProvider currentProvider, ShaderModel shaderModel, bool includePixelShaderConstructor)
        {
            return GenerateCode(currentProvider, BuildPixelShaderGraph(shaderModel, includePixelShaderConstructor));
        }

        public static Assembly CompileInMemory(string code)
        {
            using (var provider = new CSharpCodeProvider(new Dictionary<string, string> { { "CompilerVersion", "v3.5" } }))
            {
                var options = new CompilerParameters();
                options.ReferencedAssemblies.Add("System.dll");
                options.ReferencedAssemblies.Add("System.Core.dll");
                options.ReferencedAssemblies.Add("WindowsBase.dll");
                options.ReferencedAssemblies.Add("PresentationFramework.dll");
                options.ReferencedAssemblies.Add("PresentationCore.dll");
                options.IncludeDebugInformation = false;
                options.GenerateExecutable = false;
                options.GenerateInMemory = true;
                var results = provider.CompileAssemblyFromSource(options, code);
                return results.Errors.Count == 0
                           ? results.CompiledAssembly
                           : null;
            }
        }

        private static CodeCompileUnit BuildPixelShaderGraph(ShaderModel shaderModel, bool includePixelShaderConstructor)
        {
            // Create a new CodeCompileUnit to contain
            // the program graph.
            var codeGraph = new CodeCompileUnit();

            // Create the namespace.
            var codeNamespace = AssignNamespacesToGraph(codeGraph, shaderModel.GeneratedNamespace);

            // Create the appropriate constructor.
            var constructor = includePixelShaderConstructor ? CreatePixelShaderConstructor(shaderModel) : CreateDefaultConstructor(shaderModel);

            // Declare a new type.
            var shader = new CodeTypeDeclaration
            {
                Name = shaderModel.GeneratedClassName,
                BaseTypes =
                {
                    new CodeTypeReference("ShaderEffect")
                },
                Members =
                {
                    constructor,
                    CreateSamplerDependencyProperty(shaderModel.GeneratedClassName, "Input"),
                    CreateCLRProperty("Input", typeof(Brush), null)
                },
            };
            if (!string.IsNullOrEmpty(shaderModel.Description))
            {
                shader.Comments.Add(new CodeCommentStatement($"<summary>{shaderModel.Description}</summary>"));
            }

            // Add a dependency property and a CLR property for each of the shader's register variables.
            foreach (var register in shaderModel.Registers)
            {
                shader.Members.Add(CreateShaderRegisterDependencyProperty(shaderModel, register));
                shader.Members.Add(CreateCLRProperty(register.RegisterName, register.RegisterType, register.Description));
            }

            // Add the new type to the namespace.
            codeNamespace.Types.Add(shader);

            return codeGraph;
        }

        private static CodeMemberField CreateSamplerDependencyProperty(string className, string propertyName)
        {
            return new CodeMemberField
            {
                Type = new CodeTypeReference("DependencyProperty"),
                Name = $"{propertyName}Property",
                Attributes = MemberAttributes.Static | MemberAttributes.Public,
                InitExpression = new CodeMethodInvokeExpression
                {
                    Method = new CodeMethodReferenceExpression
                    {
                        TargetObject = new CodeTypeReferenceExpression("ShaderEffect"),
                        MethodName = "RegisterPixelShaderSamplerProperty"
                    },
                    Parameters =
                    {
                        new CodePrimitiveExpression(propertyName),
                        new CodeTypeOfExpression(className),
                        new CodePrimitiveExpression(0)
                    }
                }
            };
        }

        private static CodeMemberField CreateShaderRegisterDependencyProperty(ShaderModel shaderModel, ShaderModelConstantRegister register)
        {
            if (typeof(Brush).IsAssignableFrom(register.RegisterType))
            {
                return new CodeMemberField
                {
                    Type = new CodeTypeReference("DependencyProperty"),
                    Name = $"{register.RegisterName}Property",
                    Attributes = MemberAttributes.Public | MemberAttributes.Static,
                    InitExpression = new CodeMethodInvokeExpression
                    {
                        Method = new CodeMethodReferenceExpression
                        {
                            TargetObject = new CodeTypeReferenceExpression("ShaderEffect"),
                            MethodName = "RegisterPixelShaderSamplerProperty"
                        },
                        Parameters =
                        {
                            new CodePrimitiveExpression(register.RegisterName),
                            new CodeTypeOfExpression(shaderModel.GeneratedClassName),
                            new CodePrimitiveExpression(register.RegisterNumber)
                        }
                    }
                };
            }

            return new CodeMemberField
            {
                Type = new CodeTypeReference("DependencyProperty"),
                Name = $"{register.RegisterName}Property",
                Attributes = MemberAttributes.Public | MemberAttributes.Static,
                InitExpression = new CodeMethodInvokeExpression
                {
                    Method = new CodeMethodReferenceExpression
                    {
                        TargetObject = new CodeTypeReferenceExpression("DependencyProperty"),
                        MethodName = "Register"
                    },
                    Parameters =
                    {
                        new CodePrimitiveExpression(register.RegisterName),
                        new CodeTypeOfExpression(CreateCodeTypeReference(register.RegisterType)),
                        new CodeTypeOfExpression(shaderModel.GeneratedClassName),
                        new CodeObjectCreateExpression
                        {
                            // Silverlight doesn't have UIPropertyMetadata.
                            CreateType = new CodeTypeReference(shaderModel.TargetFramework == TargetFramework.WPF ? "UIPropertyMetadata" : "PropertyMetadata"),
                            Parameters =
                            {
                                CreateDefaultValue(register.DefaultValue),
                                new CodeMethodInvokeExpression
                                {
                                    Method = new CodeMethodReferenceExpression(null, "PixelShaderConstantCallback"),
                                    Parameters =
                                    {
                                        new CodePrimitiveExpression(register.RegisterNumber)
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }

        private static CodeExpression CreateDefaultValue(object defaultValue)
        {
            if (defaultValue == null)
            {
                return new CodePrimitiveExpression(null);
            }

            var codeTypeReference = CreateCodeTypeReference(defaultValue.GetType());
            if (defaultValue.GetType().IsPrimitive)
            {
                return new CodeCastExpression(codeTypeReference, new CodePrimitiveExpression(defaultValue));
            }

            if (defaultValue is Point || defaultValue is Vector || defaultValue is Size)
            {
                var point = (Point)RegisterValueConverter.ConvertToUsualType(defaultValue);
                return new CodeObjectCreateExpression(
                    codeTypeReference,
                    new CodePrimitiveExpression(point.X),
                    new CodePrimitiveExpression(point.Y));
            }

            if (defaultValue is Point3D || defaultValue is Vector3D)
            {
                var point3D = (Point3D)RegisterValueConverter.ConvertToUsualType(defaultValue);
                return new CodeObjectCreateExpression(
                    codeTypeReference,
                    new CodePrimitiveExpression(point3D.X),
                    new CodePrimitiveExpression(point3D.Y),
                    new CodePrimitiveExpression(point3D.Z));
            }

            if (defaultValue is Point4D)
            {
                var point4D = (Point4D)defaultValue;
                return new CodeObjectCreateExpression(
                    codeTypeReference,
                    new CodePrimitiveExpression(point4D.X),
                    new CodePrimitiveExpression(point4D.Y),
                    new CodePrimitiveExpression(point4D.Z),
                    new CodePrimitiveExpression(point4D.W));
            }

            if (defaultValue is Color)
            {
                var color = (Color)defaultValue;
                return new CodeMethodInvokeExpression(
                    new CodeTypeReferenceExpression(codeTypeReference),
                    "FromArgb",
                    new CodePrimitiveExpression(color.A),
                    new CodePrimitiveExpression(color.R),
                    new CodePrimitiveExpression(color.G),
                    new CodePrimitiveExpression(color.B));
            }

            return new CodeDefaultValueExpression(codeTypeReference);
        }

        private static CodeMemberProperty CreateCLRProperty(string propertyName, Type type, string description)
        {
            var property = new CodeMemberProperty
            {
                Name = propertyName,
                Type = CreateCodeTypeReference(type),
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
                HasGet = true,
                GetStatements =
                {
                    new CodeMethodReturnStatement
                    {
                        Expression = new CodeCastExpression
                        {
                            TargetType = CreateCodeTypeReference(type),
                            Expression = new CodeMethodInvokeExpression
                            {
                                Method = new CodeMethodReferenceExpression(new CodeThisReferenceExpression(), "GetValue"),
                                Parameters = { new CodeVariableReferenceExpression($"{propertyName}Property") }
                            }
                        }
                    }
                },
                HasSet = true,
                SetStatements =
                {
                    new CodeMethodInvokeExpression
                    {
                        Method = new CodeMethodReferenceExpression(new CodeThisReferenceExpression(), "SetValue"),
                        Parameters =
                        {
                            new CodeVariableReferenceExpression(propertyName + "Property"),
                            new CodeVariableReferenceExpression("value")
                        }
                    }
                }
            };
            if (!string.IsNullOrEmpty(description))
            {
                property.Comments.Add(new CodeCommentStatement($"<summary>{description}</summary>"));
            }

            return property;
        }

        private static CodeTypeReference CreateCodeTypeReference(Type type)
        {
            return type.IsPrimitive ? new CodeTypeReference(type) : new CodeTypeReference(type.Name);
        }

        private static CodeConstructor CreatePixelShaderConstructor(ShaderModel shaderModel)
        {
            // Create a constructor that takes a PixelShader as its only parameter.
            var constructor = new CodeConstructor
            {
                Attributes = MemberAttributes.Public,
                Parameters =
                {
                    new CodeParameterDeclarationExpression("PixelShader", "shader")
                },
                Statements =
                {
                    new CodeAssignStatement
                    {
                        Left = new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), "PixelShader"),
                        Right = new CodeArgumentReferenceExpression("shader")
                    },
                    new CodeSnippetStatement(string.Empty),
                    CreateUpdateMethod("Input")
                }
            };
            foreach (var register in shaderModel.Registers)
            {
                constructor.Statements.Add(CreateUpdateMethod(register.RegisterName));
            }

            return constructor;
        }

        private static CodeConstructor CreateDefaultConstructor(ShaderModel shaderModel)
        {
            // Create a default constructor.
            var shaderRelativeUri =
                $"/{shaderModel.GeneratedNamespace};component/{shaderModel.GeneratedClassName}.ps";
            var constructor = new CodeConstructor
            {
                Attributes = MemberAttributes.Public,
                Statements =
                {
                    new CodeVariableDeclarationStatement
                    {
                        Type = new CodeTypeReference("PixelShader"),
                        Name = "pixelShader",
                        InitExpression = new CodeObjectCreateExpression("PixelShader")
                    },
                    new CodeAssignStatement
                    {
                        Left = new CodePropertyReferenceExpression(new CodeVariableReferenceExpression("pixelShader"), "UriSource"),
                        Right = new CodeObjectCreateExpression
                        {
                            CreateType = new CodeTypeReference("Uri"),
                            Parameters =
                            {
                                new CodePrimitiveExpression(shaderRelativeUri),
                                new CodeFieldReferenceExpression(new CodeTypeReferenceExpression("UriKind"), "Relative")
                            }
                        }
                    },
                    new CodeAssignStatement
                    {
                        Left = new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), "PixelShader"),
                        Right = new CodeArgumentReferenceExpression("pixelShader")
                    },
                    new CodeSnippetStatement(string.Empty),
                    CreateUpdateMethod("Input")
                }
            };
            foreach (var register in shaderModel.Registers)
            {
                constructor.Statements.Add(CreateUpdateMethod(register.RegisterName));
            }

            return constructor;
        }

        private static CodeMethodInvokeExpression CreateUpdateMethod(string propertyName)
        {
            return new CodeMethodInvokeExpression
            {
                Method = new CodeMethodReferenceExpression(new CodeThisReferenceExpression(), "UpdateShaderValue"),
                Parameters =
                {
                    new CodeVariableReferenceExpression(propertyName + "Property")
                }
            };
        }

        private static CodeNamespace AssignNamespacesToGraph(CodeCompileUnit codeGraph, string namespaceName)
        {
            // Add imports to the global (unnamed) namespace.
            var globalNamespace = new CodeNamespace
            {
                Imports =
                {
                    new CodeNamespaceImport("System"),
                    new CodeNamespaceImport("System.Windows"),
                    new CodeNamespaceImport("System.Windows.Media"),
                    new CodeNamespaceImport("System.Windows.Media.Effects"),
                    new CodeNamespaceImport("System.Windows.Media.Media3D")
                }
            };
            codeGraph.Namespaces.Add(globalNamespace);

            // Create a named namespace.
            var ns = new CodeNamespace(namespaceName);
            codeGraph.Namespaces.Add(ns);
            return ns;
        }

        private static string GenerateCode(CodeDomProvider provider, CodeCompileUnit compileUnit)
        {
            // Generate source code using the code generator.
            using (var writer = new StringWriter())
            {
                var indentString = Settings.Default.IndentUsingTabs ? "\t" : string.Format("{0," + Settings.Default.IndentSpaces.ToString() + "}", " ");
                var options = new CodeGeneratorOptions { IndentString = indentString, BlankLinesBetweenMembers = false };
                provider.GenerateCodeFromCompileUnit(compileUnit, writer, options);
                var text = writer.ToString();
                //// Fix up code: make static DP fields readonly, and use triple-slash or triple-quote comments for XML doc comments.
                if (provider.FileExtension == "cs")
                {
                    text = text.Replace("public static DependencyProperty", "public static readonly DependencyProperty");
                    text = Regex.Replace(text, @"// <(?!/?auto-generated)", @"/// <");
                }
                else
                    if (provider.FileExtension == "vb")
                {
                    text = text.Replace("Public Shared ", "Public Shared ReadOnly ");
                    text = text.Replace("'<", "'''<");
                }

                return text;
            }
        }
    }
}

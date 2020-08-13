﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.VisualBasic;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Waf.DotNetPad.Applications.CodeAnalysis
{
    internal class ScriptingWorkspace : Workspace
    {
        private static readonly Assembly[] defaultReferences =
        {
            typeof(object).Assembly,                                // mscorelib
            typeof(Uri).Assembly,                                   // System
            typeof(Enumerable).Assembly,                            // System.Core
            typeof(XmlReader).Assembly,                             // System.Xml
            typeof(XDocument).Assembly,                             // System.Xml.Linq
            typeof(DataContractSerializer).Assembly,                // System.Runtime.Serialization
            typeof(ImmutableArray).Assembly,                        // System.Collections.Immutable
            typeof(Span<>).Assembly,                                // System.Memory
            typeof(ArrayPool<>).Assembly,                           // System.Buffers
        };

        private readonly ConcurrentDictionary<string, DocumentationProvider> documentationProviders;

        public ScriptingWorkspace(HostServices hostServices) : base(hostServices, WorkspaceKind.Host)
        {
            documentationProviders = new ConcurrentDictionary<string, DocumentationProvider>();
        }

        public override bool CanApplyChange(ApplyChangesKind feature)
        {
            return feature == ApplyChangesKind.ChangeDocument || base.CanApplyChange(feature);
        }

        public DocumentId AddProjectWithDocument(string documentFileName, string text)
        {
            var fileName = Path.GetFileName(documentFileName);
            var name = Path.GetFileNameWithoutExtension(documentFileName);
            var language = Path.GetExtension(documentFileName) == ".vb" ? LanguageNames.VisualBasic : LanguageNames.CSharp;

            var projectId = ProjectId.CreateNewId();

            var references = defaultReferences.Distinct().Select(CreateReference).ToList();
            references.Add(CreateReference(Assembly.Load("System.Runtime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")));
            references.Add(CreateReference(Assembly.Load("netstandard, Version=2.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51")));
            if (language == LanguageNames.VisualBasic) { references.Add(CreateReference(typeof(VBMath).Assembly)); }
            else if (language == LanguageNames.CSharp) { references.Add(CreateReference(typeof(RuntimeBinderException).Assembly)); }

            var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Default, name, name + ".dll", language, metadataReferences: references,
                parseOptions: language == LanguageNames.CSharp ? (ParseOptions)new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp9) 
                    : new VisualBasicParseOptions(Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.VisualBasic16));
            OnProjectAdded(projectInfo);

            if (language == LanguageNames.CSharp)
            {
                var servicesDocumentId = DocumentId.CreateNewId(projectId);
                var servicesDocumentInfo = DocumentInfo.Create(servicesDocumentId, "CompilerServices.cs", loader: TextLoader.From(TextAndVersion.Create(SourceText.From(@"
using System.ComponentModel;
namespace System.Runtime.CompilerServices
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class IsExternalInit
    {
    }
}
", Encoding.UTF8), VersionStamp.Create())));
                OnDocumentAdded(servicesDocumentInfo);
            }

            var documentId = DocumentId.CreateNewId(projectId);
            var documentInfo = DocumentInfo.Create(documentId, fileName, 
                                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(text, Encoding.UTF8), VersionStamp.Create())));
            OnDocumentAdded(documentInfo);
            return documentId;
        }

        public void RemoveProject(DocumentId id)
        {
            OnProjectRemoved(id.ProjectId);
        }

        public void UpdateText(DocumentId documentId, string text)
        {
            OnDocumentTextChanged(documentId, SourceText.From(text, Encoding.UTF8), PreservationMode.PreserveValue);
        }

        public Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                var project = CurrentSolution.GetProject(documentId.ProjectId);
                var compilation = await project!.GetCompilationAsync(cancellationToken);
                return (IReadOnlyList<Diagnostic>)compilation!.GetDiagnostics(cancellationToken);
            }, cancellationToken);
        }

        public Task<BuildResult> BuildAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                var project = CurrentSolution.GetProject(documentId.ProjectId);
                var compilation = await project!.GetCompilationAsync(cancellationToken);

                using var peStream = new MemoryStream();
                using var pdbStream = new MemoryStream();
                var result = compilation!.Emit(peStream, pdbStream);
                var inMemoryAssembly = result.Success ? peStream.ToArray() : null;
                var inMemorySymbolStore = result.Success ? pdbStream.ToArray() : null;
                return new BuildResult(result.Diagnostics, inMemoryAssembly, inMemorySymbolStore);
            }, cancellationToken);
        }
        
        public Task FormatDocumentAsync(DocumentId documentId)
        {
            return Task.Run(async () =>
            {
                var formattedDocument = await Microsoft.CodeAnalysis.Formatting.Formatter.FormatAsync(
                        CurrentSolution.GetDocument(documentId)).ConfigureAwait(false);
                TryApplyChanges(formattedDocument.Project.Solution);
            });
        }

        protected override void ApplyDocumentTextChanged(DocumentId documentId, SourceText text)
        {
            OnDocumentTextChanged(documentId, text, PreservationMode.PreserveValue);
        }

        private MetadataReference CreateReference(Assembly assembly)
        {
            string assemblyPath = assembly.Location;
            string documentationPath = Path.ChangeExtension(assemblyPath, "xml");
            var provider = documentationProviders.GetOrAdd(documentationPath, path => new FileBasedXmlDocumentationProvider(path));
            return MetadataReference.CreateFromFile(assemblyPath, new MetadataReferenceProperties(), provider);
        }
    }
}

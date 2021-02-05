// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Text;
using System.IO;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators
{
    internal class SourceGeneratorProjectItem : RazorProjectItem
    {
        private readonly string _fileKind;

        public SourceGeneratorProjectItem(string basePath, string filePath, string relativePhysicalPath, string fileKind, AdditionalText additionalText, string cssScope)
        {
            BasePath = basePath;
            FilePath = filePath;
            RelativePhysicalPath = relativePhysicalPath;
            _fileKind = fileKind;
            AdditionalText = additionalText;
            CssScope = cssScope;
            var text = AdditionalText.GetText();
        }

        public AdditionalText AdditionalText { get; }

        public override string BasePath { get; }

        public override string FilePath { get; }

        public override bool Exists => true;

        public override string PhysicalPath => AdditionalText.Path;

        public override string RelativePhysicalPath { get; }

        public override string FileKind => _fileKind ?? base.FileKind;

        public override string CssScope { get; }

        public override Stream Read() => new MemoryStream(Encoding.UTF8.GetBytes(AdditionalText.GetText().ToString()));
    }
}

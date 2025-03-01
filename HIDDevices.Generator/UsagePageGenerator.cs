﻿// Licensed under the Apache License, Version 2.0 (the "License").
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using iTextSharp.text.pdf;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json;

namespace HIDDevices.Generator;

[Generator]
public class UsagePageGenerator : ISourceGenerator
{
    private static readonly Uri s_emptyUri = new(string.Empty, UriKind.Relative);

    public void Initialize(GeneratorInitializationContext context)
    {
        // Nothing required.
    }

    public void Execute(GeneratorExecutionContext context)
    {
        var sw = Stopwatch.StartNew();
        var usages = 0;

        // For debugging uncomment the following line:
        // Debugger.Launch();

        // Get options
        var (rootNamespace, specificationUri, jsonAttachmentUri, cacheFolder, projectDir, force, maxGenerated) =
            GetOptions(context);

        // Try to get absolute Uri for cacheFolder
        var cache = cacheFolder.IsAbsoluteUri;
        if (!cache)
        {
            // Try to combine them into an absolute uri
            cache = projectDir.IsAbsoluteUri &&
                    cacheFolder != s_emptyUri &&
                    Uri.TryCreate(Path.Combine(projectDir.AbsolutePath, cacheFolder.OriginalString), UriKind.Absolute,
                        out cacheFolder);
        }

        // Check if caching folder already exists.
        if (cache && !Directory.Exists(cacheFolder.AbsolutePath))
        {
            try
            {
                // Try to create the folder
                Directory.CreateDirectory(cacheFolder.AbsolutePath);
            }
            catch (Exception exception)
            {
                context.Report(Diagnostics.CacheFolderCreationFailed, Location.None, cacheFolder, exception);
                cache = false;
            }
        }

        // Check for cancellation
        if (CheckCancelled(context))
        {
            return;
        }

        // Get absolute paths for cache files
        var cacheJson = s_emptyUri;
        var cachePdf = s_emptyUri;
        cache &= jsonAttachmentUri != s_emptyUri &&
                 specificationUri != s_emptyUri &&
                 Uri.TryCreate(Path.Combine(cacheFolder.AbsolutePath, jsonAttachmentUri.OriginalString),
                     UriKind.Absolute, out cacheJson) &&
                 Uri.TryCreate(Path.Combine(cacheFolder.AbsolutePath, Path.GetFileName(specificationUri.LocalPath)),
                     UriKind.Absolute, out cachePdf);

        // If we aren't caching, we must go to the original source.
        if (!cache)
        {
            // Blank caching file locations
            cacheJson = s_emptyUri;
            cachePdf = s_emptyUri;
            force = true;
            context.Report(Diagnostics.CachingDisabled, Location.None);
        }

        var tables = HidUsageTables.Empty;
        if (!force)
        {
            if (File.Exists(cacheJson.AbsolutePath))
            {
                // Load from the cached JSON.
                tables = LoadFromJson(context, cacheJson);
            }
            else if (File.Exists(cachePdf.AbsolutePath))
            {
                tables = LoadFromPdf(context, cachePdf, jsonAttachmentUri, cacheJson);
            }
        }

        if (tables == HidUsageTables.Empty)
        {
            // Load from source, updating Cache if appropriate
            tables = LoadFromPdf(context, specificationUri, jsonAttachmentUri, cachePdf, cacheJson);
        }

        // Create file header
        var builder = new IndentStringBuilder();
        builder.AppendComment($@"Licensed under the Apache License, Version 2.0 (the ""License"").
See the LICENSE file in the project root for more information.

Specification revision: {tables.Version}; generated at {tables.LastGenerated:u}.")
            .Append(@"
#pragma warning disable CS0108 // Member hides inherited member; missing new keyword

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
");
        var header = builder.ToString();
        builder.Clear();

        /*
         * Generate the usage page enumerations
         */
        bool first;
        foreach (var page in tables.UsagePages)
        {
            // Check for cancellation
            if (CheckCancelled(context))
            {
                return;
            }

            // Start of enum
            builder.AppendLine(header)
                .Append("namespace ").Append(rootNamespace).AppendLine(".Usages")
                .OpenBrace()
                .AppendSummary($"{page.Name} Usage Page.")
                .AppendDescription($"{page.Name} Usage Page")
                .Append("public enum ").Append(page.SafeName).AppendLine("Page : uint")
                .OpenBrace();

            first = true;
            foreach (var usage in page.UsageIds)
            {
                if (first)
                {
                    first = false;
                }
                else
                {
                    builder.AppendLine(",").AppendLine();
                }

                var fullId = (uint)(page.Id << 16) + usage.Id;
                builder.AppendSummary($"{usage.Name} Usage.")
                    .AppendDescription(usage.Name)
                    .Append(usage.SafeName).Append(" = 0x").Append(fullId.ToString("x8"));
                usages++;
            }

            var generator = page.UsageIdGenerator;
            if (generator != null && maxGenerated > 0)
            {
                if (!first)
                {
                    builder.AppendLine(",").AppendLine();
                }

                builder.AppendComment($"Range: 0x{generator.StartUsageId:x4} -> 0x{generator.EndUsageId:x4}")
                    .AppendLine();

                var prefix = generator.NamePrefix;
                var safePrefix = generator.SafeNamePrefix;
                first = true;
                for (var i = 0; i < maxGenerated; i++)
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        builder.AppendLine(",").AppendLine();
                    }

                    var id = generator.StartUsageId + i;
                    if (id > generator.EndUsageId)
                    {
                        break;
                    }


                    var fullId = (uint)(page.Id << 16) + id;
                    builder.AppendSummary($"{prefix} {i} Usage.")
                        .AppendDescription($"{prefix} {i}")
                        .Append(safePrefix).Append(i).Append(" = 0x").Append(fullId.ToString("x8"));
                    usages++;
                }
            }

            // End of enum
            builder.AppendLine()
                .CloseBrace()
                .CloseBrace();

            // Add source file
            context.AddSource($"{page.SafeName}Page.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
            builder.Clear();
        }

        /*
         * Generate the UsagePage class
         */

        // Start of class
        builder.AppendLine(header)
            .Append("namespace ").AppendLine(rootNamespace)
            .OpenBrace()
            .Append("using ").Append(rootNamespace).AppendLine(".Pages;")
            .AppendLine()
            .AppendSummary("Base class for all usage pages.")
            .AppendLine("public partial class UsagePage")
            .OpenBrace()
            .AppendSummary("Dictionary of all defined usage pages.")
            .AppendLine("private static ConcurrentDictionary<ushort, UsagePage> s_pages =")
            .Indent().Indent()
            .AppendLine("new ConcurrentDictionary<ushort, UsagePage>")
            .OpenBrace();

        first = true;
        foreach (var page in tables.UsagePages)
        {
            // Check for cancellation
            if (CheckCancelled(context))
            {
                return;
            }

            if (first)
            {
                first = false;
            }
            else
            {
                builder.AppendLine(",");
            }

            builder.Append($"[0x{page.Id:x4}] = {page.SafeName}UsagePage.Instance");
        }

        builder.AppendLine().Outdent().Append("};").Outdent().Outdent();

        foreach (var page in tables.UsagePages)
        {
            // Check for cancellation
            if (CheckCancelled(context))
            {
                return;
            }

            builder.AppendLine().AppendLine()
                .AppendSummary($"{page.Name} Usage Page.")
                .Append("public static readonly ")
                .Append(page.SafeName)
                .AppendFormat("UsagePage ")
                .Append(page.SafeName)
                .Append(" = ")
                .Append(page.SafeName)
                .AppendFormat("UsagePage.Instance;");
        }

        // End of UsagePage class
        builder.AppendLine()
            .CloseBrace()
            .CloseBrace();

        // Add source file
        context.AddSource("UsagePage.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
        builder.Clear();

        /*
         * Generate the specific UsagePage classes
         */
        foreach (var page in tables.UsagePages)
        {
            // Check for cancellation
            if (CheckCancelled(context))
            {
                return;
            }

            // Start of class
            builder.AppendLine(header)
                .Append("namespace ").Append(rootNamespace).AppendLine(".Pages")
                .OpenBrace()
                .Append("using ").Append(rootNamespace).AppendLine(".Usages;")
                .AppendLine()
                .AppendSummary($"{page.Name} Usage Page.")
                .AppendLine($"public sealed class {page.SafeName}UsagePage : UsagePage")
                .OpenBrace()
                .AppendSummary($"Singleton instance of {page.Name} Usage Page.")
                .Append("public static readonly ").Append(page.SafeName).AppendLine("UsagePage Instance = new();")
                .AppendLine()
                .AppendSummary("Create singleton.")
                .Append($"private {page.SafeName}UsagePage() : base(0x{page.Id:x4}, ")
                .AppendQuoted(page.Name)
                .AppendLine(")")
                .OpenBrace().CloseBrace()
                .AppendLine()
                .Indent("/// ").AppendLine("<inheritdoc />").Outdent()
                .AppendLine("protected override Usage CreateUsage(ushort id)")
                .OpenBrace()
                .AppendLine("switch (id)")
                .OpenBrace();

            foreach (var usage in page.UsageIds)
            {
                builder.Append($"case 0x{usage.Id:x4}: return new Usage(this, id, ")
                    .AppendQuoted(usage.Name).Append(", ");

                if (usage.Kinds.Any())
                {
                    first = true;
                    foreach (var kind in usage.Kinds)
                    {
                        if (first)
                        {
                            first = false;
                        }
                        else
                        {
                            builder.Append('|');
                        }

                        builder.Append("UsageTypes.").Append(kind.ToString());
                    }
                }
                else
                {
                    builder.Append("UsageTypes.None");
                }

                builder.AppendLine(");");
            }


            var generator = page.UsageIdGenerator;
            if (generator != null && maxGenerated > 0)
            {
                var prefix = generator.NamePrefix;

                var id = generator.StartUsageId - 1;
                for (var i = 0; i < maxGenerated; i++)
                {
                    id++;
                    builder.Append($"case 0x{id:x4}: return new Usage(this, id, ")
                        .AppendQuoted($"{prefix} {i}").Append(", ");

                    if (generator.Kinds.Any())
                    {
                        first = true;
                        foreach (var kind in generator.Kinds)
                        {
                            if (first)
                            {
                                first = false;
                            }
                            else
                            {
                                builder.Append('|');
                            }

                            builder.Append("UsageTypes.").Append(kind.ToString());
                        }
                    }
                    else
                    {
                        builder.Append("UsageTypes.None");
                    }

                    builder.AppendLine(");");
                }

                builder.CloseBrace();

                if (id < generator.EndUsageId)
                {
                    builder.AppendLine($"var n = (ushort)(id-0x{generator.StartUsageId:x4});")
                        .Append(
                            $"if (id >= 0x{id + 1:x4} || id <= 0x{generator.EndUsageId:x4}) return new Usage(this, id, $")
                        .AppendQuoted($"{prefix} {{n}}").Append(", ");

                    if (generator.Kinds.Any())
                    {
                        first = true;
                        foreach (var kind in generator.Kinds)
                        {
                            if (first)
                            {
                                first = false;
                            }
                            else
                            {
                                builder.Append('|');
                            }

                            builder.Append("UsageTypes.").Append(kind.ToString());
                        }
                    }
                    else
                    {
                        builder.Append("UsageTypes.None");
                    }

                    builder.AppendLine(");");
                }
            }
            else
            {
                builder.CloseBrace();
            }

            // End of enum
            builder.AppendLine()
                .AppendLine("return base.CreateUsage(id);")
                .CloseBrace()
                .CloseBrace()
                .CloseBrace();

            // Add source file
            context.AddSource($"{page.SafeName}UsagePage.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
            builder.Clear();
        }

        // Report the completion diagnostic - note, this currently doesn't appear anywhere!
        // See https://github.com/dotnet/roslyn/issues/50208
        context.Report(Diagnostics.Completed, Location.None, tables.Version, usages, tables.UsagePages.Count, sw);
    }

    /// <summary>
    ///     Checks for cancellation at regular intervals.
    /// </summary>
    /// <param name="context"></param>
    /// <returns><see langword="true" /> if cancellation has occurred; otherwise, <see langword="false" />.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CheckCancelled(GeneratorExecutionContext context)
    {
        if (!context.CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        context.Report(Diagnostics.Cancelled, Location.None);
        return true;
    }

    /// <summary>
    ///     Grabs various configuration options from build properties.
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    private (string rootNamespace, Uri specificationUri, Uri jsonAttachmentUri, Uri cacheFolder, Uri projectDir, bool
        force, ushort
        maxGenerated)
        GetOptions(GeneratorExecutionContext context) =>
        (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.RootNamespace",
                out var rootNamespace)
                ? rootNamespace
                : null ?? "DevDecoder.HIDDevices",
            context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.HIDUsageTablesPDF",
                out var hidUsageTablesPdf) &&
            Uri.TryCreate(hidUsageTablesPdf, UriKind.RelativeOrAbsolute, out var p)
                ? p
                : s_emptyUri,
            context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.HIDUsageTablesJSON",
                out var hidUsageTablesJson) &&
            Uri.TryCreate(hidUsageTablesJson, UriKind.Relative, out var j)
                ? j
                : s_emptyUri,
            context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.HIDUsageTablesCacheFolder",
                out var hidUsageTablesCacheFolder) &&
            Uri.TryCreate(hidUsageTablesCacheFolder, UriKind.RelativeOrAbsolute, out var c)
                ? c
                : s_emptyUri,
            context.AnalyzerConfigOptions.GlobalOptions.TryGetValue(
                "build_property.projectdir",
                out var pd) &&
            Uri.TryCreate(pd, UriKind.Absolute, out var pdu)
                ? pdu
                : Uri.TryCreate(Directory.GetCurrentDirectory(), UriKind.Absolute, out pdu)
                    ? pdu
                    : s_emptyUri,
            context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.GenerateUsagesFromSource",
                out var generateUsagesFromSource) &&
            bool.TryParse(generateUsagesFromSource, out var generateUsagesFromSourceBool) &&
            generateUsagesFromSourceBool,
            context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.HIDUsagePagesMaxGenerated",
                out var hidUsagePagesMaxGenerated) &&
            ushort.TryParse(hidUsagePagesMaxGenerated, out var maxGenerated)
                ? maxGenerated
                : (ushort)16);

    /// <summary>
    ///     Loads Json from a cached PDF (and updates the cached JSON).
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="pdfUri">Where the stream came from (used for logging).</param>
    /// <param name="jsonAttachmentUri">The Uri of the attachment inside the PDF.</param>
    /// <param name="jsonUri">The JSON cache file URI.</param>
    private HidUsageTables LoadFromPdf(GeneratorExecutionContext context, Uri pdfUri, Uri jsonAttachmentUri,
        Uri jsonUri)
        => LoadFromPdf(context, pdfUri, jsonAttachmentUri, s_emptyUri, jsonUri);

    /// <summary>
    ///     Loads Json from a cached PDF (and updates the cached JSON).
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="pdfUri">Where the stream came from (used for logging).</param>
    /// <param name="jsonAttachmentUri">The Uri of the attachment inside the PDF.</param>
    /// <param name="pdfCacheUri">Where to cache the PDF.</param>
    /// <param name="jsonUri">The JSON cache file URI.</param>
    private HidUsageTables LoadFromPdf(GeneratorExecutionContext context, Uri pdfUri, Uri jsonAttachmentUri,
        Uri pdfCacheUri, Uri jsonUri)
    {
        byte[] pdfBytes;
        try
        {
            if (pdfUri.Scheme.StartsWith("http", StringComparison.InvariantCultureIgnoreCase))
            {
                // Load from web
                using var client = new HttpClient();
                pdfBytes = client.GetByteArrayAsync(pdfUri).Result;
            }
            else if (string.Equals(pdfUri.Scheme, "file"))
            {
                // Load from cache file
                pdfBytes = File.ReadAllBytes(pdfUri.AbsolutePath);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(pdfUri), pdfUri,
                    $"The pdf Uri has an unsupported scheme '{pdfUri.Scheme}'");
            }
        }
        catch (Exception exception)
        {
            context.Report(Diagnostics.PdfNotFound, Location.None, pdfUri.AbsolutePath, exception);
            return HidUsageTables.Empty;
        }

        try
        {
            var attachmentName = jsonAttachmentUri.OriginalString;

            if (CheckCancelled(context))
            {
                return HidUsageTables.Empty;
            }

            // Cache PDF
            if (pdfCacheUri != s_emptyUri)
            {
                File.WriteAllBytes(pdfCacheUri.AbsolutePath, pdfBytes);
            }

            var reader = new PdfReader(pdfBytes);
            var catalog = reader.Catalog;
            if (PdfReader.GetPdfObject(catalog.Get(PdfName.Names)) is PdfDictionary documentNames &&
                PdfReader.GetPdfObject(documentNames.Get(PdfName.Embeddedfiles)) is PdfDictionary embeddedFiles)
            {
                var fileSpecs = embeddedFiles.GetAsArray(PdfName.Names);

                for (var i = 0; i < fileSpecs.Size; i++)
                {
                    var fileArray = fileSpecs.GetAsDict(i);
                    if (fileArray is null)
                    {
                        continue;
                    }

                    var file = fileArray.GetAsDict(PdfName.EF);
                    if (file is null)
                    {
                        continue;
                    }

                    var (key, _) = file.Keys.Where(k => k is not null)
                        .Select(k => (key: k, fileName: fileArray.GetAsString(k)?.ToString()!))
                        .Where(f => !string.IsNullOrWhiteSpace(f.fileName))
                        .OrderBy(f =>
                            string.Equals(f.fileName, attachmentName, StringComparison.InvariantCultureIgnoreCase)
                                ? 0
                                : 1)
                        .FirstOrDefault();

                    if (key is null)
                    {
                        context.Report(Diagnostics.JsonAttachmentNotFound, Location.None, pdfUri.AbsolutePath);
                        return HidUsageTables.Empty;
                    }

                    if (PdfReader.GetPdfObject(file.GetAsIndirectObject(key)) is not PrStream stream)
                    {
                        context.Report(Diagnostics.JsonAttachmentNotFound, Location.None, pdfUri.AbsolutePath);
                        return HidUsageTables.Empty;
                    }

                    if (CheckCancelled(context))
                    {
                        return HidUsageTables.Empty;
                    }

                    var data = PdfReader.GetStreamBytes(stream);

                    // Cache JSON

                    if (jsonUri != s_emptyUri)
                    {
                        File.WriteAllBytes(jsonUri.AbsolutePath, data);
                    }


                    if (CheckCancelled(context))
                    {
                        return HidUsageTables.Empty;
                    }

                    // Parse JSON
                    using var jsonStream = new MemoryStream(data, false);
                    return ParseJson(context, jsonStream);
                }
            }

            context.Report(Diagnostics.JsonAttachmentNotFound, Location.None, pdfUri.AbsolutePath);
            return HidUsageTables.Empty;
        }
        catch (Exception exception)
        {
            context.Report(Diagnostics.JsonExtractionFailed, Location.None, pdfUri.AbsolutePath, exception);
            return HidUsageTables.Empty;
        }
    }

    /// <summary>
    ///     Loads Json from a cached file.
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="jsonUri">The JSON cache file URI.</param>
    private HidUsageTables LoadFromJson(GeneratorExecutionContext context, Uri jsonUri)
    {
        try
        {
            using var jsonStream = File.OpenRead(jsonUri.AbsolutePath);
            return ParseJson(context, jsonStream);
        }
        catch (Exception exception)
        {
            context.Report(Diagnostics.JsonDeserializationFailed, Location.None, exception);
            return HidUsageTables.Empty;
        }
    }

    /// <summary>
    ///     Loads usage tables from a JSON stream.
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="jsonStream">The JSON stream.</param>
    private HidUsageTables ParseJson(GeneratorExecutionContext context, Stream jsonStream)
    {
        try
        {
            using var reader = new StreamReader(jsonStream);
            return JsonConvert.DeserializeObject<HidUsageTables>(reader.ReadToEnd()) ??
                   throw new NullReferenceException("The deserialization of JSON returned null.");
        }
        catch (Exception exception)
        {
            context.Report(Diagnostics.JsonDeserializationFailed, Location.None, exception);
            return HidUsageTables.Empty;
        }
    }
}

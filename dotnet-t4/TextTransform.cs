//
// Copyright (c) 2009 Novell, Inc. (http://www.novell.com)
// Copyright (c) Microsoft Corp. (https://www.microsoft.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TextTemplating;
using Mono.Options;

namespace Mono.TextTemplating
{
	class TextTransform
	{
		static OptionSet optionSet;

		public static int Main (string [] args)
		{
			try {
				return MainInternal (args);
			}
			catch (Exception e) {
				Console.Error.WriteLine (e);
				return -1;
			}
		}

		sealed class CustomOption : Option
		{
			readonly Action<OptionValueCollection> action;
			public CustomOption (string prototype, string description, int count, Action<OptionValueCollection> action, bool hidden = false)
				: base (prototype, description, count, hidden)
				=> this.action = action ?? throw new ArgumentNullException (nameof (action));
			protected override void OnParseComplete (OptionContext c) => action (c.OptionValues);
		}

		static int MainInternal (string [] args)
		{
			if (args.Length == 0 && !Console.IsInputRedirected) {
				ShowHelp (true);
			}

			var generator = new ToolTemplateGenerator ();
			string outputFile = null, inputFile = null;
			var properties = new Dictionary<string,string> ();
			string preprocessClassName = null;
			bool debug = false;
			bool verbose = false;

			optionSet = new OptionSet {
				{
					"o=|out=",
					"Set the name or path of the output <file>. It defaults to the input filename with its extension changed to `.txt`, " +
					"or to match the generated code when preprocessing, and may be overridden by template settings. " +
					"Use `-` instead of a filename to write to stdout.",
					s => outputFile = s
				},
				{
					"r=",
					"Add an {<assembly>} reference by path or assembly name. It will be resolved from the " +
					"framework and assembly directories.",
					s => generator.Refs.Add (s)
				},
				{
					"u=|using=",
					"Import a {<namespace>} by generating a using statement.",
					s => generator.Imports.Add (s)
				},
				{
					"I=",
					"Add a {<directory>} to be searched when resolving included files.",
					s => generator.IncludePaths.Add (s)
				},
				{
					"P=",
					"Add a {<directory>} to be searched when resolving assemblies.",
					s => generator.ReferencePaths.Add (s)
				},
				{
					"c=|class=",
					"Preprocess the template into class {<name>} for use as a runtime template. The class name may include a namespace.",
					(s) => preprocessClassName = s
				},
				{
					"l|useRelativeLinePragmas",
					"Use relative paths in line pragmas.",
					s => generator.UseRelativeLinePragmas = true
				},
				{
					"p==|parameter==",
					"Set session parameter {0:<name>} to {1:<value>}. " +
					"The value is accessed from the template's Session dictionary, " +
					"or from a property declared with a parameter directive: <#@ parameter name='<name>' type='<type>' #>. " +
					"If the <name> matches a parameter directive, the <value> will be converted to that parameter's type.",
					(k,v) => properties[k]=v
				},
				{
					"debug",
					"Generate debug symbols and keep temporary files.",
					s => debug = true
				},
				{
					"v|verbose",
					"Output additional diagnostic information to stdout.",
					s => verbose = true
				},
				{
					"h|?|help",
					"Show help",
					s => ShowHelp (false)
				},
				new CustomOption (
					"dp=!",
					"Set {0:<directive>} to be handled by directive processor {1:<class>} in {2:<assembly>}.",
					3,
					a => generator.AddDirectiveProcessor(a[0], a[1], a[2])
				),
				new CustomOption (
					"a=!=",
					"Set host parameter {2:<name>} to {3:<value>}. It may optionally be scoped to a {1:<directive>} and/or {0:<processor>}. " +
					"The value is accessed from the host's ResolveParameterValue() method " +
					"or from a property declared with a parameter directive: <#@ parameter name='<name>' #>. ",
					4,
					a => {
						if (a.Count == 2) {
							generator.AddParameter (null, null, a[0], a[1]);
						} else if (a.Count == 3) {
							generator.AddParameter (null, a[0], a[1], a[2]);

						} else {
							generator.AddParameter (a[0], a[1], a[2], a[3]);
						}
					}
				)
			};

			var remainingArgs = optionSet.Parse (args);

			string inputContent = null;
			bool inputIsFromStdin = false;

			if (remainingArgs.Count != 1) {
				if (Console.IsInputRedirected) {
					inputContent = Console.In.ReadToEnd ();
					inputIsFromStdin = true;
				} else {
					Console.Error.WriteLine ("No input file specified.");
					return 1;
				}
			} else {
				inputFile = remainingArgs [0];
				if (!File.Exists (inputFile)) {
					Console.Error.WriteLine ("Input file '{0}' does not exist.", inputFile);
					return 1;
				}
			}

			bool writeToStdout = outputFile == "-" || (inputIsFromStdin && string.IsNullOrEmpty (outputFile));
			bool isDefaultOutputFilename = false;

			if (!writeToStdout && string.IsNullOrEmpty (outputFile)) {
				outputFile = inputFile;
				isDefaultOutputFilename = true;
				if (Path.HasExtension (outputFile)) {
					var dir = Path.GetDirectoryName (outputFile);
					var fn = Path.GetFileNameWithoutExtension (outputFile);
					outputFile = Path.Combine (dir, fn + ".txt");
				} else {
					outputFile = outputFile + ".txt";
				}
			}

			if (inputFile != null) {
				try {
					inputContent = File.ReadAllText (inputFile);
				}
				catch (IOException ex) {
					Console.Error.WriteLine ("Could not read input file '" + inputFile + "':\n" + ex);
					return 1;
				}
			}

			if (inputContent.Length == 0) {
				Console.Error.WriteLine ("Input is empty");
				return 1;
			}

			var pt = generator.ParseTemplate(inputFile, inputContent);

			TemplateSettings settings = TemplatingEngine.GetSettings (generator, pt);
			if (debug) {
				settings.Debug = true;
			}
			if (verbose) {
				settings.Log = Console.Out;
			}

			if (pt.Errors.Count > 0) {
				generator.Errors.AddRange (pt.Errors);
			}

			string outputContent = null;
			if (!generator.Errors.HasErrors) {
				AddCoercedSessionParameters (generator, pt, properties);
			}

			if (!generator.Errors.HasErrors) {
				if (preprocessClassName == null) {
					(outputFile, outputContent) = generator.ProcessTemplateAsync (pt, inputFile, inputContent, outputFile, settings).Result;
				} else {
					SplitClassName (preprocessClassName, settings);
					outputContent = generator.PreprocessTemplate (pt, inputFile, inputContent, settings, out _);
					if (isDefaultOutputFilename) {
						outputFile = Path.ChangeExtension (outputFile, settings.Provider.FileExtension);
					}
				}
			}

			if (generator.Errors.HasErrors) {
				Console.Error.WriteLine (inputFile == null ? "Processing failed." : $"Processing '{inputFile}' failed.");
			}

			try {
				if (!generator.Errors.HasErrors) {
					if (writeToStdout) {
						Console.WriteLine (outputContent);
					} else {
						File.WriteAllText (outputFile, outputContent, new UTF8Encoding (encoderShouldEmitUTF8Identifier: false));
					}
				}
			}
			catch (IOException ex) {
				Console.Error.WriteLine ("Could not write output file '" + outputFile + "':\n" + ex);
				return 1;
			}

			LogErrors (generator);

			return generator.Errors.HasErrors ? 1 : 0;
		}

		static void SplitClassName (string className, TemplateSettings settings)
		{
			int s = className.LastIndexOf ('.');
			if (s > 0) {
				settings.Namespace = className.Substring (0, s);
				settings.Name = className.Substring (s + 1);
			}
		}

		static void AddCoercedSessionParameters (ToolTemplateGenerator generator, ParsedTemplate pt, Dictionary<string, string> properties)
		{
			if (properties.Count == 0) {
				return;
			}

			var session = generator.GetOrCreateSession ();

			foreach (var p in properties) {
				var directive = pt.Directives.FirstOrDefault (d =>
					d.Name == "parameter" &&
					d.Attributes.TryGetValue ("name", out string attVal) &&
					attVal == p.Key);

				if (directive != null) {
					directive.Attributes.TryGetValue ("type", out string typeName);
					var mappedType = ParameterDirectiveProcessor.MapTypeName (typeName);
					if (mappedType != "System.String") {
						if (ConvertType (mappedType, p.Value, out object converted)) {
							session [p.Key] = converted;
							continue;
						}

						generator.Errors.Add (
							new CompilerError (
								null, 0, 0, null,
								$"Could not convert property '{p.Key}'='{p.Value}' to parameter type '{typeName}'"
							)
						);
					}
				}
				session [p.Key] = p.Value;
			}
		}

		static bool ConvertType (string typeName, string value, out object converted)
		{
			converted = null;
			try {
				var type = Type.GetType (typeName);
				if (type == null) {
					return false;
				}
				Type stringType = typeof (string);
				if (type == stringType) {
					return true;
				}
				var converter = System.ComponentModel.TypeDescriptor.GetConverter (type);
				if (converter == null || !converter.CanConvertFrom (stringType)) {
					return false;
				}
				converted = converter.ConvertFromString (value);
				return true;
			}
			catch {
			}
			return false;
		}

		static void LogErrors (TemplateGenerator generator)
		{
			foreach (CompilerError err in generator.Errors) {
				var oldColor = Console.ForegroundColor;
				Console.ForegroundColor = err.IsWarning? ConsoleColor.Yellow : ConsoleColor.Red;
				if (!string.IsNullOrEmpty (err.FileName)) {
					Console.Error.Write (err.FileName);
				}
				if (err.Line > 0) {
					Console.Error.Write ("(");
					Console.Error.Write (err.Line);
					if (err.Column > 0) {
						Console.Error.Write (",");
						Console.Error.Write (err.Column);
					}
					Console.Error.Write (")");
				}
				if (!string.IsNullOrEmpty (err.FileName) || err.Line > 0) {
					Console.Error.Write (": ");
				}
				Console.Error.Write (err.IsWarning ? "WARNING: " : "ERROR: ");
				Console.Error.WriteLine (err.ErrorText);
				Console.ForegroundColor = oldColor;
			}
		}

		static void ShowHelp (bool concise)
		{
			var name = Path.GetFileNameWithoutExtension (Assembly.GetExecutingAssembly ().Location);
			Console.WriteLine ("T4 text template processor version {0}", ThisAssembly.AssemblyInformationalVersion);
			Console.WriteLine ("Usage: {0} [options] [template-file]", name);
			if (concise) {
				Console.WriteLine ("Use --help to display options.");
			} else {
				Console.WriteLine ();
				Console.WriteLine ("The template-file argument is required unless the template text is piped in via stdin.");
				Console.WriteLine ();
				Console.WriteLine ("Options:");
				Console.WriteLine ();
				optionSet.WriteOptionDescriptions (Console.Out);
				Console.WriteLine ();
				Environment.Exit (0);
			}
		}
	}
}
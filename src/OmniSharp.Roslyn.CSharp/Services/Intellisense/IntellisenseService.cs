using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Recommendations;
using Microsoft.CodeAnalysis.Text;
using OmniSharp.Extensions;
using OmniSharp.Mef;
using OmniSharp.Models.AutoComplete;
using OmniSharp.Options;
using OmniSharp.Roslyn.CSharp.Services.Documentation;

namespace OmniSharp.Roslyn.CSharp.Services.Intellisense
{
    [OmniSharpHandler(OmniSharpEndpoints.AutoComplete, LanguageNames.CSharp)]
    public class IntellisenseService : IRequestHandler<AutoCompleteRequest, IEnumerable<AutoCompleteResponse>>
    {
        private readonly OmniSharpWorkspace _workspace;
        private readonly FormattingOptions _formattingOptions;

        [ImportingConstructor]
        public IntellisenseService(OmniSharpWorkspace workspace, FormattingOptions formattingOptions)
        {
            _workspace = workspace;
            _formattingOptions = formattingOptions;
        }

        public async Task<IEnumerable<AutoCompleteResponse>> Handle(AutoCompleteRequest request)
        {
            var documents = _workspace.GetDocuments(request.FileName);
            var wordToComplete = request.WordToComplete;
            var completions = new HashSet<AutoCompleteResponse>();

            foreach (var document in documents)
            {
                var sourceText = await document.GetTextAsync();
                var position = sourceText.Lines.GetPosition(new LinePosition(request.Line, request.Column));
                var service = CompletionService.GetService(document);
                var completionList = await service.GetCompletionsAsync(document, position);

                if (completionList != null)
                {
                    // get recommened symbols to match them up later with SymbolCompletionProvider
                    var semanticModel = await document.GetSemanticModelAsync();
                    var recommendedSymbols = await Recommender.GetRecommendedSymbolsAtPositionAsync(semanticModel, position, _workspace);

                    foreach (var item in completionList.Items)
                    {
                        var completionText = item.DisplayText;
                        if (completionText.IsValidCompletionFor(wordToComplete))
                        {
                            var symbols = await item.GetCompletionSymbolsAsync(recommendedSymbols, document);
                            if (symbols.Any())
                            {
                                foreach (var symbol in symbols)
                                {
                                    if (symbol != null)
                                    {
                                        if (request.WantSnippet)
                                        {
                                            foreach (var completion in MakeSnippetedResponses(request, symbol, item.DisplayText))
                                            {
                                                completions.Add(completion);
                                            }
                                        }
                                        else
                                        {
                                            completions.Add(MakeAutoCompleteResponse(request, symbol, item.DisplayText));
                                        }
                                    }
                                }

                                // if we had any symbols from the completion, we can continue, otherwise it means
                                // the completion didn't have an associated symbol so we'll add it manually
                                continue;
                            }

                            // for other completions, i.e. keywords, create a simple AutoCompleteResponse
                            // we'll just assume that the completion text is the same
                            // as the display text.
                            var response = new AutoCompleteResponse()
                            {
                                CompletionText = item.DisplayText,
                                DisplayText = item.DisplayText,
                                Snippet = item.DisplayText,
                                Kind = request.WantKind ? item.Tags.First() : null
                            };

                            completions.Add(response);
                        }
                    }
                }
            }

            return completions
                .OrderByDescending(c => c.CompletionText.IsValidCompletionStartsWithExactCase(wordToComplete))
                .ThenByDescending(c => c.CompletionText.IsValidCompletionStartsWithIgnoreCase(wordToComplete))
                .ThenByDescending(c => c.CompletionText.IsCamelCaseMatch(wordToComplete))
                .ThenByDescending(c => c.CompletionText.IsSubsequenceMatch(wordToComplete))
                .ThenBy(c => c.DisplayText, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.CompletionText, StringComparer.OrdinalIgnoreCase);
        }

        private IEnumerable<AutoCompleteResponse> MakeSnippetedResponses(AutoCompleteRequest request, ISymbol symbol, string displayName)
        {
            var completions = new List<AutoCompleteResponse>();

            var methodSymbol = symbol as IMethodSymbol;
            if (methodSymbol != null)
            {
                if (methodSymbol.Parameters.Any(p => p.IsOptional))
                {
                    completions.Add(MakeAutoCompleteResponse(request, symbol, displayName, false));
                }

                completions.Add(MakeAutoCompleteResponse(request, symbol, displayName));

                return completions;
            }

            var typeSymbol = symbol as INamedTypeSymbol;
            if (typeSymbol != null)
            {
                completions.Add(MakeAutoCompleteResponse(request, symbol, displayName));

                if (typeSymbol.TypeKind != TypeKind.Enum)
                {
                    foreach (var ctor in typeSymbol.InstanceConstructors)
                    {
                        completions.Add(MakeAutoCompleteResponse(request, ctor, displayName));
                    }
                }

                return completions;
            }

            return new[] { MakeAutoCompleteResponse(request, symbol, displayName) };
        }

        private AutoCompleteResponse MakeAutoCompleteResponse(AutoCompleteRequest request, ISymbol symbol, string displayName, bool includeOptionalParams = true)
        {
            var displayNameGenerator = new SnippetGenerator();
            displayNameGenerator.IncludeMarkers = false;
            displayNameGenerator.IncludeOptionalParameters = includeOptionalParams;

            var response = new AutoCompleteResponse();
            response.CompletionText = displayName;

            // TODO: Do something more intelligent here
            response.DisplayText = displayNameGenerator.Generate(symbol);

            if (request.WantDocumentationForEveryCompletionResult)
            {
                response.Description = DocumentationConverter.ConvertDocumentation(symbol.GetDocumentationCommentXml(), _formattingOptions.NewLine);
            }

            if (request.WantReturnType)
            {
                response.ReturnType = ReturnTypeFormatter.GetReturnType(symbol);
            }

            if (request.WantKind)
            {
                response.Kind = symbol.GetKind();
            }

            if (request.WantSnippet)
            {
                var snippetGenerator = new SnippetGenerator();
                snippetGenerator.IncludeMarkers = true;
                snippetGenerator.IncludeOptionalParameters = includeOptionalParams;
                response.Snippet = snippetGenerator.Generate(symbol);
            }

            if (request.WantMethodHeader)
            {
                response.MethodHeader = displayNameGenerator.Generate(symbol);
            }

            return response;
        }
    }
}

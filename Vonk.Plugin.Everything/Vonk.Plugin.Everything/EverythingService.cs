using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Specification;
using Hl7.FhirPath;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vonk.Core.Common;
using Vonk.Core.Context;
using Vonk.Core.ElementModel;
using Vonk.Core.Repository;
using Vonk.Core.Support;
using static Vonk.Core.Context.VonkOutcome;
using Task = System.Threading.Tasks.Task;

namespace Vonk.Plugin.EverythingOperation
{
    public class EverythingService
    {
        private readonly ISearchRepository _searchRepository;
        private readonly IResourceChangeRepository _changeRepository;
        private readonly IStructureDefinitionSummaryProvider _schemaProvider;
        private readonly ILogger<EverythingService> _logger;

        public EverythingService(ISearchRepository searchRepository,
            IResourceChangeRepository changeRepository,
            IStructureDefinitionSummaryProvider schemaProvider,
            ILogger<EverythingService> logger)
        {
            Check.NotNull(searchRepository, nameof(searchRepository));
            Check.NotNull(changeRepository, nameof(changeRepository));
            Check.NotNull(logger, nameof(logger));
            _searchRepository = searchRepository;
            _changeRepository = changeRepository;
            _schemaProvider = schemaProvider;
            _logger = logger;
        }

        /// <summary>
        /// Handle GET [base]/Patient/id/$everything
        /// </summary>
        /// <param name="vonkContext">IVonkContext for details of the request and providing the response</param>
        /// <returns></returns>
        public async Task PatientInstanceGET(IVonkContext vonkContext)
        {
            var patientID = vonkContext.Arguments.ResourceIdArgument().ArgumentValue;
            await EverytingBundle(vonkContext, patientID);
        }

        /// <summary>
        /// Create a new FHIR Search bundle: add the Patient resource as a match, as $everything is a search operation.
        /// Additionally, include all resources found through references in the Patient resource.
        /// Only a single Patient resource is currently considered (the resource upon which $everything is called).
        /// </summary>
        /// <param name="vonkContext"></param>
        /// <returns></returns>
        public async Task EverytingBundle(IVonkContext vonkContext, string patientID)
        {
            // Build empty everything result bundle
            var everythingBundle = CreateEmptyBundle();

            vonkContext.Arguments.Handled(); // Signal to Vonk -> Mark arguments as "done"

            // Get Patient resource
            (var patientResolved, var resolvedResource, var error) = await ResolveResource(patientID, "Patient");
            if (patientResolved)
            {
                if (resolvedResource.InformationModel != vonkContext.InformationModel)
                {
                    CancelEverythingOperation(vonkContext, StatusCodes.Status415UnsupportedMediaType, WrongInformationModel(vonkContext.InformationModel, resolvedResource));
                    return;
                }

                // Include Patient resource in search results
                everythingBundle = everythingBundle.AddEntry(resolvedResource, "Patient/" + patientID);

                // Recursively resolve and include all references in the search bundle, overwrite documentBundle as GenericBundle is immutable
                (_, everythingBundle, error) = await IncludeReferencesInBundle(resolvedResource, everythingBundle);
            }

            // Handle responses
            if (!(error is null))
            {
                if (!patientResolved) // Patient resource, on which the operation is called, does not exist
                {
                    _logger.LogTrace("$everythingt called on non-existing Patient/{id}", patientID);
                    CancelEverythingOperation(vonkContext, StatusCodes.Status404NotFound);
                }
                else if (error.Equals(VonkIssue.PROCESSING_ERROR))
                {
                    _logger.LogTrace("$everything failed to include resource in correct information model", patientID);
                    CancelEverythingOperation(vonkContext, StatusCodes.Status415UnsupportedMediaType, error);
                }
                else // Local or external reference reference could not be found
                {
                    CancelEverythingOperation(vonkContext, StatusCodes.Status500InternalServerError, error);
                }
                return;
            }

            // Check if we need to persist the bundle
            var persistArgument = vonkContext.Arguments.GetArgument("persist");
            var userRequestedPersistOption = persistArgument == null ? String.Empty : persistArgument.ArgumentValue;
            if (userRequestedPersistOption.Equals("true"))
            {
                await _changeRepository.Create(everythingBundle.ToIResource(vonkContext.InformationModel));
            }

            SendCreatedDocument(vonkContext, everythingBundle); // Return newly created bundle
        }

        /// <summary>
        /// Include all resources found through references in a resource in a search bundle. 
        /// This function traverses recursively through all references until no new references are found.
        /// No depth-related limitations.
        /// </summary>
        /// <param name="startResource">First resource which potentially contains references that need to be included in the document</param>
        /// <param name="searchBundle">FHIR Search Bundle to which the resolved resources shall be added as includes</param>
        /// <returns>
        /// - success describes if all references could recursively be found, starting from the given resource
        /// - failedReference contains the first reference that could not be resolved, empty if all resources can be resolved
        /// </returns>
        private async Task<(bool success, GenericBundle documentBundle, VonkIssue error)> IncludeReferencesInBundle(IResource startResource, GenericBundle searchBundle)
        {
            var includedReferences = new HashSet<string>();
            return await IncludeReferencesInBundle(startResource, searchBundle, includedReferences);
        }

        /// <summary>
        /// Overloaded method for recursive use.
        /// </summary>
        /// <param name="resource"></param>
        /// <param name="documentBundle"></param>
        /// <param name="includedReferences">Remember which resources were already added to the search bundle</param>
        /// <returns></returns>
        private async Task<(bool success, GenericBundle documentBundle, VonkIssue error)> IncludeReferencesInBundle(IResource resource, GenericBundle documentBundle, HashSet<string> includedReferences)
        {
            // Get references of given resource
            var allReferencesInResourceQuery = "$this.descendants().where($this is Reference).reference";
            var references = resource.ToTypedElement(_schemaProvider).Select(allReferencesInResourceQuery);

            // Resolve references
            // Skip the following resources: 
            //    - Contained resources as they are already included through their parents
            //    - Resources that are already included in the search bundle
            (bool successfulResolve, IResource resolvedResource, VonkIssue error) = (true, null, null);
            foreach (var reference in references)
            {
                var referenceValue = reference.Value.ToString();
                if (!referenceValue.StartsWith("#", StringComparison.Ordinal) && !includedReferences.Contains(referenceValue))
                {
                    (successfulResolve, resolvedResource, error) = await ResolveResource(referenceValue);
                    if(successfulResolve){

                        if(resource.InformationModel != resolvedResource.InformationModel)
                        {
                            return (false, documentBundle, WrongInformationModel(resource.InformationModel, resolvedResource));
                        }
                           
                        documentBundle = documentBundle.AddEntry(resolvedResource, referenceValue);
                        includedReferences.Add(referenceValue);
                    }
                    else
                    {
                        break;
                    }

                    // Recursively resolve all references in the included resource
                    (successfulResolve, documentBundle, error) = await IncludeReferencesInBundle(resolvedResource, documentBundle, includedReferences);
                    if(!successfulResolve){
                        break;
                    }
                }
            }
            return (successfulResolve, documentBundle, error);
        }

        #region Helper - Bundle-related

        private GenericBundle CreateEmptyBundle()
        {
            var bundleResourceNode = SourceNode.Resource("Bundle", "Bundle", SourceNode.Valued("type", "searchset"));

            var identifier = SourceNode.Node("identifier");
            identifier.Add(SourceNode.Valued("system", "urn:ietf:rfc:3986"));
            identifier.Add(SourceNode.Valued("value", Guid.NewGuid().ToString()));
            bundleResourceNode.Add(identifier);

            var documentBundle = GenericBundle.FromBundle(bundleResourceNode);
            documentBundle = documentBundle.Meta(Guid.NewGuid().ToString(), DateTimeOffset.Now);

            return documentBundle;
        }

        #endregion Helper - Bundle-related

        #region Helper - Resolve resources

        private async Task<(bool success, IResource resolvedResource, VonkIssue failedReference)> ResolveResource(string id, string type)
        {
            return await ResolveResource(type + "/" + id);
        }

        private async Task<(bool success, IResource resolvedResource, VonkIssue failedReference)> ResolveResource(string reference)
        {
            if (IsRelativeUrl(reference))
                return await ResolveLocalResource(reference);

            // Server chooses not to handle absolute (remote) references
            return (false, null, ReferenceNotResolvedIssue(reference, false));
        }

        private async Task<(bool success, IResource resolvedResource, VonkIssue failedReference)> ResolveLocalResource(string reference)
        {
            var result = await _searchRepository.GetByKey(ResourceKey.Parse(reference));
            if (result == null)
                return (false, null, ReferenceNotResolvedIssue(reference, true));

            return (true, result, null);
        }

        private bool IsRelativeUrl(string reference)
        {
            return Uri.IsWellFormedUriString(reference, UriKind.Relative);
        }

        #endregion Helper - Resolve resources

        #region Helper - Return response

        private void SendCreatedDocument(IVonkContext vonkContext, GenericBundle searchBundle)
        {
            vonkContext.Response.Payload = searchBundle.ToIResource(vonkContext.InformationModel);
            vonkContext.Response.HttpResult = 200;
            vonkContext.Response.Headers.Add(VonkResultHeader.Location, "Bundle/" + vonkContext.Response.Payload.Id);
        }

        private void CancelEverythingOperation(IVonkContext vonkContext, int statusCode, VonkIssue failedReference = null)
        {
            vonkContext.Response.HttpResult = statusCode;
            if(failedReference != null)
                vonkContext.Response.Outcome.AddIssue(failedReference);
        }

        #endregion Helper - Return response

        private VonkIssue ReferenceNotResolvedIssue(string failedReference, bool missingReferenceIsLocal)
        {
            VonkIssue issue;
            if (missingReferenceIsLocal)
            {
                issue = new VonkIssue(IssueSeverity.Error, IssueType.NotFound, "MSG_LOCAL_FAIL", $"Unable to resolve local reference to resource {failedReference}");
            }
            else
            {
                issue = new VonkIssue(IssueSeverity.Error, IssueType.NotSupported, "MSG_EXTERNAL_FAIL", $"Resolving external resource references ({failedReference}) is not supported");
            }

            issue.DetailCodeSystem = "http://vonk.fire.ly/fhir/ValueSet/OperationOutcomeIssueDetails";
            return issue;
        }

        private VonkIssue WrongInformationModel(string expectedInformationModel, IResource resolvedResource)
        {
            return new VonkIssue(VonkIssue.PROCESSING_ERROR.Severity, VonkIssue.PROCESSING_ERROR.IssueType, details: $"Found {resolvedResource.Type}/{resolvedResource.Id} in information model {resolvedResource.InformationModel}. Expected information model {expectedInformationModel} instead.");
        }

    }
}

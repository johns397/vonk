using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Vonk.Core.Common;
using Vonk.Core.Context;
using Vonk.Core.ElementModel;
using Vonk.Core.Metadata;
using Vonk.Core.Model;
using Vonk.Core.Repository;
using Vonk.Fhir.R4;
using Vonk.UnitTests.Framework.Helpers;
using Xunit;
using static Vonk.UnitTests.Framework.Helpers.LoggerUtils;
using Task = System.Threading.Tasks.Task;

namespace Vonk.Plugin.EverythingOperation.Test
{
    /*   $everything unit tests:
            - (X) $everything should return HTTP 200 and a valid FHIR patient (GET)
            - (X) $everything should return HTTP 404 when being called on a missing patient         
            - (X) $everything should persist the generated patient on request
            - (X) $everything should return an INVALID_REQUEST when being called with POST and an missing id
            - (X) $everything should throw an internal server error if a local reference to a resource, which should be included in the patient, can't be found.
            - (X) $everything should throw an internal server error if an external reference is requested to be included in the patient
    */

    public class EverythingOperationTests
    {
        private EverythingService _everythingService;

        private ILogger<EverythingService> _logger = Logger<EverythingService>();
        private Mock<ISearchRepository> _searchMock = new Mock<ISearchRepository>();
        private Mock<IResourceChangeRepository> _changeMock = new Mock<IResourceChangeRepository>();
        private IStructureDefinitionSummaryProvider _schemaProvider = new PocoStructureDefinitionSummaryProvider();
        private Mock<IModelService> _modelService = new Mock<IModelService>();

        public EverythingOperationTests()
        {
            _everythingService = new EverythingService(_searchMock.Object, _changeMock.Object, _schemaProvider, _logger, _modelService.Object);
        }

        [Fact]
        public async Task VerifyServerMetaInfoForEverythingOperation()
        {
            string baseURL = "http://localhost:4080";

            // Send metaprofile request to server to verify that the metadata contains the Patient-everywhere operation
            var client = new FhirClient(baseURL);
            client.PreferredFormat = ResourceFormat.Json;
            client.UseFormatParam = true;

            // Read the Vonk server's metadata (NOT working!! - getting error:
            // System.NotSupportedException : Cannot read the conformance statement of the server to verify FHIR version compatibility
            //var capabilityStatement = await client.CapabilityStatementAsync();
            string metadata = $"{baseURL}/metadata";
            var capabilityStatement = await client.GetAsync(metadata);
            // Now checke that the "Patient-everything" operation is there
            var everythigOperation = capabilityStatement.ToJObject();
        }

        [Fact]
        public void BuildTestBundle()
        {
            var bundle = CreateBundle();

            string patientId = "test";
            var iResource = CreateTestPatientNoReferences(patientId);

            var entry = new Bundle.EntryComponent();
            var patient = PocoBuilderExtensions.ToPoco<Patient>(iResource);
            entry.FullUrl = $"/{patient.ResourceType.ToString()}/{patientId}";
            entry.Resource = patient;
            entry.Request = new Bundle.RequestComponent()
            {
                Method = Bundle.HTTPVerb.PUT,
                Url = entry.Resource.TypeName,
                IfMatch = $"/{entry.Resource.TypeName}?identifier={patientId}"
            };
            //entry.Request = new Bundle.RequestComponent() { Method = Bundle.HTTPVerb.POST, Url = entry.Resource.TypeName, IfNoneExist = $"identifier={entry.FullUrl}" };
            bundle.Entry.Add(entry);

            string resourceId = "acct1";
            iResource = CreateTestAccountForPatient(resourceId, patientId);
            Account account = PocoBuilderExtensions.ToPoco<Account>(iResource);
            entry = new Bundle.EntryComponent();
            entry.FullUrl = $"/{account.ResourceType.ToString()}/{resourceId}";
            entry.Resource = account;
            entry.Request = new Bundle.RequestComponent()
            {
                Method = Bundle.HTTPVerb.PUT,
                Url = entry.Resource.TypeName,
                IfMatch = $"/{entry.Resource.TypeName}?identifier={resourceId}"
            };
            //entry.Request = new Bundle.RequestComponent() { Method = Bundle.HTTPVerb.POST, Url = entry.Resource.TypeName, IfNoneExist = $"identifier={entry.FullUrl}" };
            bundle.Entry.Add(entry);

            resourceId = "obs1";
            iResource = CreateTestObservation(resourceId, "hr-code", 72, "bpm", patientId);
            entry = new Bundle.EntryComponent();
            Observation observation = PocoBuilderExtensions.ToPoco<Observation>(iResource);
            entry.FullUrl = $"/{observation.ResourceType.ToString()}/{resourceId}";
            entry.Resource = observation;
            entry.Request = new Bundle.RequestComponent()
            {
                Method = Bundle.HTTPVerb.PUT,
                Url = entry.Resource.TypeName,
                IfMatch = $"/{entry.Resource.TypeName}?identifier={resourceId}"
            };
            //entry.Request = new Bundle.RequestComponent() { Method = Bundle.HTTPVerb.POST, Url = entry.Resource.TypeName, IfNoneExist = $"identifier={entry.FullUrl}" };
            bundle.Entry.Add(entry);

            resourceId = "obs2";
            iResource = CreateTestObservation(resourceId, "temp-code", 36.8M, "C", patientId);
            entry = new Bundle.EntryComponent();
            observation = PocoBuilderExtensions.ToPoco<Observation>(iResource);
            entry.FullUrl = $"/{observation.ResourceType.ToString()}/{resourceId}";
            entry.Resource = observation;
            entry.Request = new Bundle.RequestComponent()
            {
                Method = Bundle.HTTPVerb.PUT,
                Url = entry.Resource.TypeName,
                IfMatch = $"/{entry.Resource.TypeName}?identifier={resourceId}"
            };
            //entry.Request = new Bundle.RequestComponent() { Method = Bundle.HTTPVerb.POST, Url = entry.Resource.TypeName, IfNoneExist = $"identifier={entry.FullUrl}" };
            bundle.Entry.Add(entry);

            string bundleStr = FhirAsJsonString(bundle);
        }

        [Fact]
        public async Task PatientOperationGETReturn200OnSuccess()
        {
            // Setup Patient resource
            var patient = CreateTestPatientNoReferences();
            string patientStr = FhirAsJsonString(patient);

            var searchResult = new SearchResult(new List<IResource>() { patient }, 1, 1);
            _searchMock.Setup(repo => repo.Search(It.IsAny<IArgumentCollection>(), It.IsAny<SearchOptions>())).ReturnsAsync(searchResult);

            // Create VonkContext for $everything (GET / Instance level)
            var testContext = new VonkTestContext(VonkInteraction.instance_custom);
            testContext.Arguments.AddArguments(new[]
            {
                new Argument(ArgumentSource.Path, ArgumentNames.resourceType, "Patient"),
                new Argument(ArgumentSource.Path, ArgumentNames.resourceId, "test")
            });
            testContext.TestRequest.CustomOperation = "everything";
            testContext.TestRequest.Method = "GET";


            VonkCompartmentDefinition componentDefinition = new VonkCompartmentDefinition() { CompartmentType = ResourceType.Patient.ToString(), InformationModel = patient.InformationModel };

            List<VonkCompartmentDefinition> vonkCompartmentDefinitionList = new List<VonkCompartmentDefinition>() { componentDefinition };
            //IModelService modelService = new IModelService() testContext.InformationModel, testContext.InformationModel, new List<string> { ResourceType.Patient.ToString() },
            //    new Dictionary<string, string>(), new List<VonkSearchParameter>(), vonkCompartmentDefinitionList);

            // Execute $everything
            await _everythingService.PatientInstanceGET(testContext);

            // Check response status
            testContext.Response.HttpResult.Should().Be(StatusCodes.Status200OK, "$everything should succeed with HTTP 200 - OK on test patient");
            testContext.Response.Payload.Should().NotBeNull();
            var bundleType = testContext.Response.Payload.SelectText("type");
            bundleType.Should().Be("searchset", "Bundle.type should be set to 'searchset'");
            var bundle = testContext.Response.Payload.ToPoco<Bundle>();
            bundle.Entry.Count.Should().Be(1);
            bundle.Entry[0].Resource.ResourceType.Should().Be(ResourceType.Patient);
            string bundleStr = FhirAsJsonString(testContext.Response.Payload);
        }

        [Fact]
        public async Task EverythingOperationGETReturn200OnSuccess()
        {
            // Create VonkContext for $everything (GET / Instance level)
            var testContext = new VonkTestContext(VonkInteraction.instance_custom);

            // Setup Patient resource
            var patient = CreateTestPatientNoReferences();
            string patientStr = FhirAsJsonString(patient);

            var account = CreateTestAccountForPatient();

            var searchOptions = SearchOptions.Latest(testContext.ServerBase, testContext.Request.Interaction, testContext.InformationModel);
            var patSearchResult = new SearchResult(new List<IResource>() { patient }, 1, 1);
            var searchArgs = new ArgumentCollection(
                new Argument(ArgumentSource.Internal, ArgumentNames.resourceType, patient.GetResourceTypeIndicator()) { MustHandle = true },
                new Argument(ArgumentSource.Internal, ArgumentNames.resourceId, $"{patient.Id}") { MustHandle = true }
            );
            _searchMock.Setup(repo => repo.Search(searchArgs, searchOptions)).ReturnsAsync(patSearchResult);

            var acctsearchResult = new SearchResult(new List<IResource>() { account }, 1, 1);
            searchArgs = new ArgumentCollection(
                new Argument(ArgumentSource.Internal, ArgumentNames.resourceType, account.GetResourceTypeIndicator()) { MustHandle = true },
                new Argument(ArgumentSource.Internal, "subject", $"Patient/{patient.Id}") { MustHandle = true }
            );

            _searchMock.Setup(repo => repo.Search(searchArgs, searchOptions)).ReturnsAsync(acctsearchResult);

            testContext.Arguments.AddArguments(new[]
            {
                new Argument(ArgumentSource.Path, ArgumentNames.resourceType, "Patient"),
                new Argument(ArgumentSource.Path, ArgumentNames.resourceId, "test")
            });
            testContext.TestRequest.CustomOperation = "everything";
            testContext.TestRequest.Method = "GET";

            // Execute $everything
            await _everythingService.PatientInstanceGET(testContext);

            // Check response status
            testContext.Response.HttpResult.Should().Be(StatusCodes.Status200OK, "$everything should succeed with HTTP 200 - OK on test patient");
            testContext.Response.Payload.Should().NotBeNull();
            var bundleType = testContext.Response.Payload.SelectText("type");
            bundleType.Should().Be("searchset", "Bundle.type should be set to 'searchset'");
            var bundle = testContext.Response.Payload.ToPoco<Bundle>();
            bundle.Entry.Count.Should().Be(1);
            bundle.Entry[0].Resource.ResourceType.Should().Be(ResourceType.Patient);
            string bundleStr = FhirAsJsonString(testContext.Response.Payload);
        }

        [Fact]
        public async Task EverythingOperationGETReturn404MissingComposition()
        {
            // Let ISearchRepository return no Patient
            var patient = CreateTestPatientNoReferences();
            var searchResult = new SearchResult(new List<IResource>(), 0, 0);
            _searchMock.Setup(repo => repo.Search(It.IsAny<IArgumentCollection>(), It.IsAny<SearchOptions>())).ReturnsAsync(searchResult);

            // Create VonkContext for $everything (GET / Instance level)
            var testContext = new VonkTestContext(VonkInteraction.instance_custom);
            testContext.Arguments.AddArguments(new[]
            {
                new Argument(ArgumentSource.Path, ArgumentNames.resourceType, "Patient"),
                new Argument(ArgumentSource.Path, ArgumentNames.resourceId, "test")
            });
            testContext.TestRequest.CustomOperation = "everything";
            testContext.TestRequest.Method = "GET";

            // Execute $everything without creating a Composition first
            await _everythingService.PatientInstanceGET(testContext);

            // Check response status
            testContext.Response.HttpResult.Should().Be(StatusCodes.Status404NotFound, "$everything should return HTTP 404 - Not found when called on a missing patient");
        }

        [Fact]
        public async Task EverythingOperationShouldPersistBundle()
        {
            // Setup Patient resource
            var patient = CreateTestPatientNoReferences();
            var searchResult = new SearchResult(new List<IResource>() { patient }, 1, 1);
            _searchMock.Setup(repo => repo.Search(It.IsAny<IArgumentCollection>(), It.IsAny<SearchOptions>())).ReturnsAsync(searchResult);

            // Create VonkContext for $everything (GET / Instance level)
            var testContext = new VonkTestContext(VonkInteraction.instance_custom);
            testContext.Arguments.AddArguments(new[]
            {
                new Argument(ArgumentSource.Path, ArgumentNames.resourceType, "Patient"),
                new Argument(ArgumentSource.Path, ArgumentNames.resourceId, "test"),
                new Argument(ArgumentSource.Query, "persist", "true")
            });
            testContext.TestRequest.CustomOperation = "everything";
            testContext.TestRequest.Method = "GET";

            // Execute $everything
            await _everythingService.PatientInstanceGET(testContext);

            _changeMock.Verify(c => c.Create(It.IsAny<IResource>()), Times.Once);
        }

        [Fact]
        public async Task EverythingOperationInternalServerErrorOnMissingReference1()
        {
            var resourceToBeFound = new List<string> { "Patient", "Orginization" };

            // Setup Patient resource
            var patient = CreateTestPatient(); // Unresolvable reference (organization resource) in the patient resource (1. level)
            var patientSearchResult = new SearchResult(new List<IResource>() { patient }, 1, 1);

            var orginization = CreateTestOrganization();
            var orginizationSearchResult = new SearchResult(new List<IResource>() { orginization }, 1, 1);

            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Patient")), It.IsAny<SearchOptions>())).ReturnsAsync(patientSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("c")), It.IsAny<SearchOptions>())).ReturnsAsync(orginizationSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => !resourceToBeFound.Contains(arg.GetArgument("_type").ArgumentValue)), It.IsAny<SearchOptions>())).ReturnsAsync(new SearchResult(Enumerable.Empty<IResource>(), 0, 0)); // -> GetBeyKey returns null

            // Create VonkContext for $everything (GET / Instance level)
            var testContext = new VonkTestContext(VonkInteraction.instance_custom);
            testContext.Arguments.AddArguments(new[]
            {
                new Argument(ArgumentSource.Path, ArgumentNames.resourceType, "Patient"),
                new Argument(ArgumentSource.Path, ArgumentNames.resourceId, "test")
            });
            testContext.TestRequest.CustomOperation = "everything";
            testContext.TestRequest.Method = "GET";

            // Execute $everything
            await _everythingService.PatientInstanceGET(testContext);

            // Check response status
            testContext.Response.HttpResult.Should().Be(StatusCodes.Status500InternalServerError, "$everything should return HTTP 500 - Internal Server error when a reference which is referenced by the patient can't be resolved");
            testContext.Response.Outcome.Issues.Should().Contain(issue => issue.IssueType.Equals(VonkOutcome.IssueType.NotFound), "OperationOutcome should explicitly mention that the reference could not be found");
        }

        [Fact]
        public async Task EverythingOperationInternalServerErrorOnMissingReference2()
        {
            var resourceToBeFound = new List<string> { "Patient", "Practitioner" };

            // Setup Composition resource
            var patient = CreateTestPatientIncOrganization(); // Unresolvable reference (Practitioner resource) in patient resource (2. level)
            var patientSearchResult = new SearchResult(new List<IResource>() { patient }, 1, 1);

            var practitioner = CreateTestPractitioner();
            var practitionerSearchResult = new SearchResult(new List<IResource>() { practitioner }, 1, 1);

            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Patient")), It.IsAny<SearchOptions>())).ReturnsAsync(patientSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Practitioner")), It.IsAny<SearchOptions>())).ReturnsAsync(practitionerSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => !resourceToBeFound.Contains(arg.GetArgument("_type").ArgumentValue)), It.IsAny<SearchOptions>())).ReturnsAsync(new SearchResult(Enumerable.Empty<IResource>(), 0, 0)); // -> GetBeyKey returns null

            // Create VonkContext for $everything (GET / Instance level)
            var testContext = new VonkTestContext(VonkInteraction.instance_custom);
            testContext.Arguments.AddArguments(new[]
            {
                new Argument(ArgumentSource.Path, ArgumentNames.resourceType, "Patient"),
                new Argument(ArgumentSource.Path, ArgumentNames.resourceId, "test")
            });
            testContext.TestRequest.CustomOperation = "everything";
            testContext.TestRequest.Method = "GET";

            // Execute $everything
            await _everythingService.PatientInstanceGET(testContext);

            // Check response status
            testContext.Response.HttpResult.Should().Be(StatusCodes.Status500InternalServerError, "$everything should return HTTP 500 - Internal Server error when a reference which is referenced by the composition can't be resolved");
            testContext.Response.Outcome.Issues.Should().Contain(issue => issue.IssueType.Equals(VonkOutcome.IssueType.NotFound), "OperationOutcome should explicitly mention that the reference could not be found");
        }

//        [Fact]
        public async Task EverythingOperationInternalServerErrorOnMissingReference3()
        {
            var resourceToBeFound = new List<string> { "Patient", "Organization", "Practitioner" };

            // Setup Patient resource
            var patient = CreateTestIncOrginizationAndPractitioner(); // Unresolvable reference (Practitioner resource) in Patient resource (3. level)
            var patientSearchResult = new SearchResult(new List<IResource>() { patient }, 1, 1);
            var patString = FhirAsJsonString(patient);

            var organization = CreateTestOrganization();
            var organizationSearchResults = new SearchResult(new List<IResource> { organization }, 1, 1);

            var practitioner = CreateTestPractitioner("missingId");
            var practitionerSearchResult = new SearchResult(new List<IResource>() { practitioner }, 1, 1);
            var pracString = FhirAsJsonString(practitioner);

            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Patient")), It.IsAny<SearchOptions>())).ReturnsAsync(patientSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Organization")), It.IsAny<SearchOptions>())).ReturnsAsync(organizationSearchResults);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Practitioner")), It.IsAny<SearchOptions>())).ReturnsAsync(practitionerSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => !resourceToBeFound.Contains(arg.GetArgument("_type").ArgumentValue)), It.IsAny<SearchOptions>())).ReturnsAsync(new SearchResult(Enumerable.Empty<IResource>(), 0, 0)); // -> GetBeyKey returns null

            // Create VonkContext for $everything (GET / Instance level)
            var testContext = new VonkTestContext(VonkInteraction.instance_custom);
            testContext.Arguments.AddArguments(new[]
            {
                new Argument(ArgumentSource.Path, ArgumentNames.resourceType, "Patient"),
                new Argument(ArgumentSource.Path, ArgumentNames.resourceId, "test")
            });
            testContext.TestRequest.CustomOperation = "everything";
            testContext.TestRequest.Method = "GET";

            // Execute $everything
            await _everythingService.PatientInstanceGET(testContext);

            // Check response status
            testContext.Response.HttpResult.Should().Be(StatusCodes.Status500InternalServerError, "$everything should return HTTP 500 - Internal Server error when a reference which is referenced by the composition can't be resolved");
            testContext.Response.Outcome.Issues.Should().Contain(issue => issue.IssueType.Equals(VonkOutcome.IssueType.NotFound), "OperationOutcome should explicitly mention that the reference could not be found");
        }

        [Fact]
        public async Task EverythingOperationSuccessCompletePatientReferences()
        {
            // Setup Patient resource
            var patient = CreateTestIncOrginizationAndPractitioner();
            var patientSearchResult = new SearchResult(new List<IResource>() { patient }, 1, 1);

            var organization = CreateTestOrganization();
            var orginzationSearchResult = new SearchResult(new List<IResource>() { organization }, 1, 1);

            var practitioner = CreateTestPractitioner();
            var practitionerSearchResult = new SearchResult(new List<IResource> { practitioner }, 1, 1);

            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Patient")), It.IsAny<SearchOptions>())).ReturnsAsync(patientSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Organization")), It.IsAny<SearchOptions>())).ReturnsAsync(orginzationSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Practitioner")), It.IsAny<SearchOptions>())).ReturnsAsync(practitionerSearchResult);

            // Create VonkContext for $everything (GET / Instance level)
            var testContext = new VonkTestContext(VonkInteraction.instance_custom);
            testContext.Arguments.AddArguments(new[]
            {
                new Argument(ArgumentSource.Path, ArgumentNames.resourceType, "Patient"),
                new Argument(ArgumentSource.Path, ArgumentNames.resourceId, "test")
            });
            testContext.TestRequest.CustomOperation = "everything";
            testContext.TestRequest.Method = "GET";

            // Execute $everything
            await _everythingService.PatientInstanceGET(testContext);

            // Check response status
            testContext.Response.HttpResult.Should().Be(StatusCodes.Status200OK, "$everything should return HTTP 200 - OK when all references in the patient (incl. recursive references) can be resolved");
            testContext.Response.Payload.SelectNodes("entry.resource").Count().Should().Be(3, "Expected Patient, Organization and Practitioner resources to be in the everything");
            var resources = testContext.Response.Payload.SelectNodes("entry");
            resources.Select(r => r.SelectNodes("resourceType").Equals(ResourceType.Patient)).Should().NotBeEmpty();
            resources.Select(r => r.SelectNodes("resourceType").Equals(ResourceType.Organization)).Should().NotBeEmpty();
            resources.Select(r => r.SelectNodes("resourceType").Equals(ResourceType.Practitioner)).Should().NotBeEmpty();
        }

        [Fact]
        public async Task EverythingOperationInternalServerErrorOnExternalReference()
        {
            // Setup Patient resource
            var patient = CreateTestPatientAbsoulteReferences(); // External reference (organization resource) in the patient resource
            var patientSearchResult = new SearchResult(new List<IResource>() { patient }, 1, 1);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Patient")), It.IsAny<SearchOptions>())).ReturnsAsync(patientSearchResult);

            // Create VonkContext for $everything (GET / Instance level)
            var testContext = new VonkTestContext(VonkInteraction.instance_custom);
            testContext.Arguments.AddArguments(new[]
            {
                new Argument(ArgumentSource.Path, ArgumentNames.resourceType, "Patient"),
                new Argument(ArgumentSource.Path, ArgumentNames.resourceId, "test")
            });
            testContext.TestRequest.CustomOperation = "everything";
            testContext.TestRequest.Method = "GET";

            // Execute $everything
            await _everythingService.PatientInstanceGET(testContext);

            // Check response status
            testContext.Response.HttpResult.Should().Be(StatusCodes.Status500InternalServerError, "$everything should return HTTP 500 - Internal Server error when an external reference is referenced by the composition");
            testContext.Response.Outcome.Issues.Should().Contain(issue => issue.IssueType.Equals(VonkOutcome.IssueType.NotSupported), "OperationOutcome should highlight that this feature is not supported");
        }

        [Fact]
        public async Task PatientBundleContainsIdentifier()
        {
            // Setup Composition resource
            var composition = CreateTestPatientNoReferences();
            var searchResult = new SearchResult(new List<IResource>() { composition }, 1, 1);
            _searchMock.Setup(repo => repo.Search(It.IsAny<IArgumentCollection>(), It.IsAny<SearchOptions>())).ReturnsAsync(searchResult);

            // Create VonkContext for $everything (GET / Instance level)
            var testContext = new VonkTestContext(VonkInteraction.instance_custom);
            testContext.Arguments.AddArguments(new[]
            {
                new Argument(ArgumentSource.Path, ArgumentNames.resourceType, "Composition"),
                new Argument(ArgumentSource.Path, ArgumentNames.resourceId, "test")
            });
            testContext.TestRequest.CustomOperation = "everything";
            testContext.TestRequest.Method = "GET";

            // Execute $everything
            await _everythingService.PatientInstanceGET(testContext);

            testContext.Response.HttpResult.Should().Be(StatusCodes.Status200OK, "$everythingt should succeed with HTTP 200 - OK on test patient");
            testContext.Response.Payload.Should().NotBeNull();

            var identifier = testContext.Response.Payload.SelectNodes("identifier");
            identifier.Should().NotBeEmpty("A Patient SHALL contain at least one identifier");
        }

        // $everything is expected to fail if a resource reference is missing, this should be checked on all levels of recursion.
        // Therefore, we build multiple resources, each with different unresolvable references

        private IResource CreateTestPatientNoReferences(string id = "test")
        {
            return new Patient() {
                //Id = id,
                VersionId = "v1",
                Identifier = new List<Identifier>() { new Identifier("PAT-SYS", id) },
            }.ToIResource();
        }

        private IResource CreateTestPatientAbsoulteReferences()
        {
           return new Patient() { Id = "test", VersionId = "v1", ManagingOrganization = new ResourceReference("http://test.firely.com/org1") }.ToIResource();
        }

        private IResource CreateTestPatientIncOrganization()
        {
            return new Patient() { Id = "test", VersionId = "v1", ManagingOrganization = new ResourceReference("Organization/org1") }.ToIResource();
        }

        private IResource CreateTestAccountForPatient(string id = "acct1", string patientRef = "test")
        {
            var account = new Account()
            {
                //Id = id,
                Identifier = new List<Identifier>() { new Identifier("ACCT-SYS", id) },
                Status = Account.AccountStatus.Active,
                VersionId = "v1",
                Subject = { new ResourceReference($"Patient/{patientRef}") },
                Name = "MyBillingAccount"
            };

            return account.ToIResource();
        }

        private IResource CreateTestIncOrginizationAndPractitioner()
        {
            var patient = new Patient() { Id = "test", VersionId = "v1",
                ManagingOrganization = new ResourceReference("Organization/org1"),
                GeneralPractitioner = new List<ResourceReference>() { new ResourceReference("Practitioner/gp1") }
            };

            return patient.ToIResource();
        }

        private IResource CreateTestPatient()
        {
            var patient = new Patient { Id = "test" };
            patient.GeneralPractitioner.Add(new ResourceReference("Practitioner/gp1"));

            return patient.ToIResource();
        }

        private IResource CreateTestList()
        {
            var list = new List { Id = "test" };
            var entryComponent = new List.EntryComponent();
            entryComponent.Item = new ResourceReference("MedicationStatement/test");
            list.Entry.Add(entryComponent);

            return list.ToIResource();
        }

        private IResource CreateTestOrganization()
        {
            return new Organization { Id = "org1", Name = "MyVonkTestOrg" }.ToIResource();
        }

        private IResource CreateTestObservation(string id, string code, decimal value, string unit, string patientRef)
        {
            Element valueQuantity = new Quantity(value, unit);
            var observation = new Observation()
            {
                //Id = id,
                Identifier = new List<Identifier>() { new Identifier("Obs-Id", id) },
                Subject = new ResourceReference($"Patient/{patientRef}"),
                Status = ObservationStatus.Final,
                Code = new CodeableConcept("loinc", code),
                Value = valueQuantity,
                // can't get Value set to valueQuantity
                //Value = new QuantityValue()
            }.ToIResource();
            return observation;
        }

        private IResource CreateTestPractitioner(string id = "gp1")
        {
            HumanName humanName = new HumanName() { Family = "Prac", Given = new List<string>() { "James" }, Prefix = new List<string>() { "Dr." } };

            return new Practitioner
            {
                Id = id,
                Name = new List<HumanName>() { humanName }
            }.ToIResource();
        }

        private Bundle CreateBundle(Bundle.BundleType? bundleType = Bundle.BundleType.Transaction)
        {
            return new Bundle() { Id = "test", VersionId = "v1", Type = bundleType };
        }

        private string FhirAsJsonString(Bundle bundle)
        {
            var serializer = new FhirJsonSerializer(new SerializerSettings()
            {
                Pretty = true
            });
            return serializer.SerializeToString(bundle);
        }

        private string FhirAsJsonString(IResource fhirObj)
        {
            var serializer = new FhirJsonSerializer(new SerializerSettings()
            {
                Pretty = true
            });
            return serializer.SerializeToString(PocoBuilderExtensions.ToPoco(fhirObj));
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Vonk.Core.Common;
using Vonk.Core.Context;
using Vonk.Core.ElementModel;
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

        public EverythingOperationTests()
        {
            _everythingService = new EverythingService(_searchMock.Object, _changeMock.Object, _schemaProvider, _logger);
        }

        [Fact]
        public async Task PatientOperationGETReturn200OnSuccess()
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
            var resourceToBeFound = new List<string> { "Organization" };

            // Setup Patient resource
            var patient = CreateTestPatientIncOrganization(); // Unresolvable reference (organization resource) in the patient resource (1. level)
            var patientSearchResult = new SearchResult(new List<IResource>() { patient }, 1, 1);
            Patient pat = patient as Patient;
            //var patString = FhirAsJsonString(pat);

            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Organization")), It.IsAny<SearchOptions>())).ReturnsAsync(patientSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => !resourceToBeFound.Contains(arg.GetArgument("_type").ArgumentValue)), It.IsAny<SearchOptions>())).ReturnsAsync(new SearchResult(Enumerable.Empty<IResource>(), 0, 0)); // -> GetBeyKey returns null

            // Create VonkContext for $everything (GET / Instance level)
            var testContext = new VonkTestContext(VonkInteraction.instance_custom);
            testContext.Arguments.AddArguments(new[]
            {
                new Argument(ArgumentSource.Path, ArgumentNames.resourceType, "Organization"),
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
            var resourceToBeFound = new List<string> { "Composition", "Patient" };

            // Setup Composition resource
            var composition = CreateTestPatientIncOrganization(); // Unresolvable reference (Practitioner resource) in patient resource (2. level)
            var compositionSearchResult = new SearchResult(new List<IResource>() { composition }, 1, 1);

            var patient = CreateTestPatient();
            var patientSearchResult = new SearchResult(new List<IResource>() { patient }, 1, 1);

            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Composition")), It.IsAny<SearchOptions>())).ReturnsAsync(compositionSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Patient")), It.IsAny<SearchOptions>())).ReturnsAsync(patientSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => !resourceToBeFound.Contains(arg.GetArgument("_type").ArgumentValue)), It.IsAny<SearchOptions>())).ReturnsAsync(new SearchResult(Enumerable.Empty<IResource>(), 0, 0)); // -> GetBeyKey returns null

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

            // Check response status
            testContext.Response.HttpResult.Should().Be(StatusCodes.Status500InternalServerError, "$everything should return HTTP 500 - Internal Server error when a reference which is referenced by the composition can't be resolved");
            testContext.Response.Outcome.Issues.Should().Contain(issue => issue.IssueType.Equals(VonkOutcome.IssueType.NotFound), "OperationOutcome should explicitly mention that the reference could not be found");
        }

        [Fact]
        public async Task EverythingOperationInternalServerErrorOnMissingReference3()
        {
            var resourceToBeFound = new List<string> { "Composition", "List", "MedicationStatement" };

            // Setup Composition resource
            var composition = CreateTestCompositionInclList(); // Unresolvable reference (Medication resource) in MedicationStatement resource (4. level)
            var compositionSearchResult = new SearchResult(new List<IResource>() { composition }, 1, 1);

            var list = CreateTestList();
            var listSearchResults = new SearchResult(new List<IResource> { list }, 1, 1);

            var medcationStatement = CreateTestMedicationStatement();
            var medcationStatementSearchResult = new SearchResult(new List<IResource>() { medcationStatement }, 1, 1);

            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Composition")), It.IsAny<SearchOptions>())).ReturnsAsync(compositionSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("List")), It.IsAny<SearchOptions>())).ReturnsAsync(listSearchResults);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("MedicationStatement")), It.IsAny<SearchOptions>())).ReturnsAsync(medcationStatementSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => !resourceToBeFound.Contains(arg.GetArgument("_type").ArgumentValue)), It.IsAny<SearchOptions>())).ReturnsAsync(new SearchResult(Enumerable.Empty<IResource>(), 0, 0)); // -> GetBeyKey returns null

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

            // Check response status
            testContext.Response.HttpResult.Should().Be(StatusCodes.Status500InternalServerError, "$everything should return HTTP 500 - Internal Server error when a reference which is referenced by the composition can't be resolved");
            testContext.Response.Outcome.Issues.Should().Contain(issue => issue.IssueType.Equals(VonkOutcome.IssueType.NotFound), "OperationOutcome should explicitly mention that the reference could not be found");
        }

        [Fact]
        public async Task EverythingOperationSuccessCompleteComposition()
        {
            // Setup Composition resource
            var composition = CreateTestCompositionInclList();
            var compositionSearchResult = new SearchResult(new List<IResource>() { composition }, 1, 1);

            var list = CreateTestList();
            var listSearchResults = new SearchResult(new List<IResource> { list }, 1, 1);

            var medcationStatement = CreateTestMedicationStatement();
            var medcationStatementSearchResult = new SearchResult(new List<IResource>() { medcationStatement }, 1, 1);

            var medication = CreateTestMedication();
            var medicationSearchResult = new SearchResult(new List<IResource> { medication }, 1, 1);

            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Composition")), It.IsAny<SearchOptions>())).ReturnsAsync(compositionSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("List")), It.IsAny<SearchOptions>())).ReturnsAsync(listSearchResults);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("MedicationStatement")), It.IsAny<SearchOptions>())).ReturnsAsync(medcationStatementSearchResult);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Medication")), It.IsAny<SearchOptions>())).ReturnsAsync(medcationStatementSearchResult);

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
            testContext.Response.Payload.SelectNodes("entry.resource").Count().Should().Be(4, "Expected Composition, List, MedicationStatement and Medication resources to be in the everything");
        }

        [Fact]
        public async Task EverythingOperationInternalServerErrorOnExternalReference()
        {
            // Setup Composition resource
            var composition = CreateTestCompositionAbsoulteReferences(); // External reference (patient resource) in the composition resource
            var compositionSearchResult = new SearchResult(new List<IResource>() { composition }, 1, 1);
            _searchMock.Setup(repo => repo.Search(It.Is<IArgumentCollection>(arg => arg.GetArgument("_type").ArgumentValue.Equals("Composition")), It.IsAny<SearchOptions>())).ReturnsAsync(compositionSearchResult);

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

        private IResource CreateTestPatientNoReferences()
        {
            return new Patient() { Id = "test", VersionId = "v1" }.ToIResource();
        }

        private IResource CreateTestCompositionAbsoulteReferences()
        {
            return new Composition() { Id = "test", VersionId = "v1", Subject = new ResourceReference("https://vonk.fire.ly/Patient/test") }.ToIResource();
        }

        private IResource CreateTestPatientIncOrganization()
        {
            return new Patient() { Id = "test", VersionId = "v1", ManagingOrganization = new ResourceReference("Organization/org1") }.ToIResource();
        }

        private IResource CreateTestCompositionInclList()
        {
            var composition = new Composition() { Id = "test", VersionId = "v1" };
            var sectionComponent = new Composition.SectionComponent();
            sectionComponent.Entry.Add(new ResourceReference("List/test"));
            composition.Section.Add(sectionComponent);

            return composition.ToIResource();
        }

        private IResource CreateTestPatient()
        {
            var patient = new Patient { Id = "test" };
            patient.GeneralPractitioner.Add(new ResourceReference("Practitioner/missing"));

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

        private IResource CreateTestMedicationStatement()
        {
            var medication = new ResourceReference("Medication/test");
            return new MedicationStatement { Id = "test", Medication = medication }.ToIResource();
        }

        private IResource CreateTestMedication()
        {
            return new Medication { Id = "test" }.ToIResource();
        }

        private IResource CreateBundle()
        {
            return new Bundle() { Id = "test", VersionId = "v1" }.ToIResource();
        }

        private string FhirAsJsonString(Base fhirObj)
        {
            var serializer = new FhirJsonSerializer(new SerializerSettings()
            {
                Pretty = true
            });
            return serializer.SerializeToString(fhirObj);
        }

    }
}

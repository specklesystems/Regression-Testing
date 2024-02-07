# nullable enable
namespace TestAutomateFunction;

using Speckle.Automate.Sdk.Schema;
using Speckle.Automate.Sdk;
using Speckle.Core.Api;
using Speckle.Core.Credentials;
using Speckle.Core.Models;
using Speckle.Core.Transports;
using Utils = TestAutomateUtils;

[TestFixture]
public sealed class AutomationContextTest : IDisposable
{
  // Regression testing function project id
  public string projectId = "6ada180f0c";

  // e2e release branch and model id
  public string releaseBranchName = "e2e/release";
  public string releaseModelId = "e4c8cc3802";

  // e2e testing branch and model id
  public string testingBranchName = "e2e/testing";
  public string testingModelId = "c374f0aade";

  private async Task<AutomationRunData> AutomationRunData(
    Base testObject,
    Base releaseObject
  )
  {
    // send the release commit to the release branch
    string releaseObjId = await Operations.Send(
      releaseObject,
      new List<ITransport> { new ServerTransport(client.Account, projectId) }
    );

    string releaseVersionId = await client.CommitCreate(
      new()
      {
        streamId = projectId,
        objectId = releaseObjId,
        branchName = releaseBranchName
      }
    );

    // send the test object to the test branch
    string testObjId = await Operations.Send(
      testObject,
      new List<ITransport> { new ServerTransport(client.Account, projectId) }
    );

    string testingVersionId = await client.CommitCreate(
      new()
      {
        streamId = projectId,
        objectId = testObjId,
        branchName = testingBranchName
      }
    );

    // create a new automation on the testing branch
    var automationName = TestAutomateUtils.RandomString(10);
    var automationId = TestAutomateUtils.RandomString(10);
    var automationRevisionId = TestAutomateUtils.RandomString(10);

    await TestAutomateUtils.RegisterNewAutomation(
      projectId,
      testingModelId,
      client,
      automationId,
      automationName,
      automationRevisionId
    );

    var automationRunId = TestAutomateUtils.RandomString(10);
    var functionId = TestAutomateUtils.RandomString(10);
    var functionName = "Automation name " + TestAutomateUtils.RandomString(10);
    var functionRelease = TestAutomateUtils.RandomString(10);

    return new AutomationRunData
    {
      ProjectId = projectId,
      ModelId = testingModelId,
      BranchName = testingBranchName,
      VersionId = testingVersionId,
      SpeckleServerUrl = client.ServerUrl,
      AutomationId = automationId,
      AutomationRevisionId = automationRevisionId,
      AutomationRunId = automationRunId,
      FunctionId = functionId,
      FunctionName = functionName,
      FunctionRelease = functionRelease,
    };
  }

  private Client client;
  private Account account;

  private string GetSpeckleToken()
  {
    var envVarName = "SPECKLE_TOKEN";
    Environment.SetEnvironmentVariable(envVarName, "");
    var token = Environment.GetEnvironmentVariable(envVarName);
    if (token is null)
    {
      throw new Exception(
        $"Cannot run tests without a {envVarName} environment variable"
      );
    }

    return token;
  }

  private string GetSpeckleServerUrl() =>
    Environment.GetEnvironmentVariable("SPECKLE_SERVER_URL")
    ?? "https://latest.speckle.systems";

  [OneTimeSetUp]
  public void Setup()
  {
    account = new Account
    {
      token = GetSpeckleToken(),
      serverInfo = new ServerInfo { url = GetSpeckleServerUrl() }
    };
    client = new Client(account);
  }

  [Test]
  public async Task TestFunctionRun()
  {
    var automationRunData = await AutomationRunData(
      TestAutomateUtils.TestObject(),
      TestAutomateUtils.ReleaseObject()
    );
    var automationContext = await AutomationRunner.RunFunction(
      AutomateFunction.Run,
      automationRunData,
      account.token,
      new FunctionInputs { DiffBranch = releaseBranchName }
    );

    Assert.That(automationContext.RunStatus, Is.EqualTo("FAILED"));
  }

  public void Dispose()
  {
    client.Dispose();
  }
}

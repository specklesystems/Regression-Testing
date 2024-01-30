using Objects;
using Speckle.Automate.Sdk;
using Speckle.Automate.Sdk.Schema;
using Speckle.Core.Api;
using Speckle.Core.Models;
using Speckle.Core.Models.Extensions;
using Speckle.Core.Transports;

static class AutomateFunction
{
  public static string ADDED = "Added";
  public static string MODIFIED = "Modified";
  public static string DELETED = "Deleted";

  public static async Task Run(
    AutomationContext automationContext,
    FunctionInputs functionInputs
  )
  {
    Console.WriteLine("Starting execution");
    _ = typeof(ObjectsKit).Assembly; // INFO: Force objects kit to initialize

    // get the test and release branch name
    var testBranchName = automationContext.AutomationRunData.BranchName;
    var releaseBranchName = testBranchName.Replace("/testing", "/release");
    Branch? releaseBranch = await automationContext.SpeckleClient
      .BranchGet(automationContext.AutomationRunData.ProjectId, releaseBranchName, 1)
      .ConfigureAwait(false);
    if (releaseBranch is null)
    {
      throw new Exception("Release branch was null");
    }

    Commit releaseCommit = releaseBranch.commits.items.First();
    if (releaseCommit is null)
    {
      throw new Exception("Release branch has no commits");
    }

    Console.WriteLine($"Comparing {testBranchName} against {releaseBranchName}");

    // get the test and release commits
    Console.WriteLine("Receiving test version");
    Base? testingCommitObject = await automationContext.ReceiveVersion();
    Console.WriteLine("Received test version: " + testingCommitObject);
    Console.WriteLine("Receiving release version");
    using ServerTransport transport =
      new(
        automationContext.SpeckleClient.Account,
        automationContext.AutomationRunData.ProjectId
      );
    Base? releaseCommitObject = await Operations
      .Receive(releaseCommit.referencedObject, transport)
      .ConfigureAwait(continueOnCapturedContext: false);
    if (releaseCommitObject == null)
    {
      throw new Exception("Commit root object was null");
    }

    Console.WriteLine("Received release version: " + releaseCommitObject);

    // flatten both commits
    IEnumerable<Base> releaseCommitObjects = releaseCommitObject.Flatten();
    IEnumerable<Base> testCommitObjects = testingCommitObject.Flatten();
    var releaseCommitObjectsDict = new Dictionary<string, Base>();
    foreach (var releaseObject in releaseCommitObjects)
    {
      if (!releaseCommitObjectsDict.ContainsKey(releaseObject.applicationId))
      {
        releaseCommitObjectsDict.Add(releaseObject.applicationId, releaseObject);
      }
    }

    Console.WriteLine(
      $"Found {releaseCommitObjects.Count()} objects in release version"
    );
    Console.WriteLine($"Found {testCommitObjects.Count()} objects in release version");

    // compare objects
    int addedCount = 0;
    int modifiedCount = 0;
    int unchangedCount = 0;
    foreach (Base testObject in testCommitObjects)
    {
      if (releaseCommitObjectsDict.ContainsKey(testObject.applicationId))
      {
        Base releaseObject = releaseCommitObjectsDict[testObject.applicationId];

        // if these have the same hash, no properties have changed
        if (testObject.id == releaseObject.id)
        {
          unchangedCount++;
        }
        // if ids are different, find property differences
        else
        {
          modifiedCount++;
          var diffDictionary = new Dictionary<string, string>();
          Dictionary<string, object> releaseObjectPropDict = releaseObject.GetMembers();
          Dictionary<string, object> testObjectPropDict = testObject.GetMembers();
          foreach (var entry in testObjectPropDict)
          {
            if (releaseObjectPropDict.ContainsKey(entry.Key))
            {
              bool changed = false;
              try
              {
                changed = entry.Value != releaseObjectPropDict[entry.Key];
              }
              catch { }
              if (changed)
              {
                string diff =
                  $"Property ({entry.Key}) changed from ({releaseObjectPropDict[entry.Key]}) to ({entry.Value})";
                if (!diffDictionary.ContainsKey(entry.Key))
                {
                  diffDictionary.Add(entry.Key, diff);
                }
              }

              releaseObjectPropDict.Remove(entry.Key);
            }
            else
            {
              if (!diffDictionary.ContainsKey(entry.Key))
              {
                diffDictionary.Add(entry.Key, ADDED);
              }
            }
          }

          // check if there are any props left on the release object - these were missing in the test object
          foreach (var entry in releaseObjectPropDict)
          {
            if (!diffDictionary.ContainsKey(entry.Key))
            {
              diffDictionary.Add(entry.Key, DELETED);
            }
          }

          // add the diff dict info to the object
          if (diffDictionary.Count > 0)
          {
            var sb = new System.Text.StringBuilder();
            foreach (var entry in diffDictionary)
            {
              sb.AppendLine($"{entry.Key}: {entry.Value}");
            }

            automationContext.AttachWarningToObjects(
              MODIFIED,
              new List<string>() { testObject.id },
              sb.ToString()
            );
          }
        }

        releaseCommitObjectsDict.Remove(testObject.applicationId);
      }
      else
      {
        addedCount++;
        automationContext.AttachInfoToObjects(
          ADDED,
          new List<string>() { testObject.id }
        );
      }
    }

    // if there are any remaining release commit objects, this indicates missing objects in the test run.
    // mark run as failed and list missing object details.
    if (releaseCommitObjectsDict.Keys.Count > 0)
    {
      automationContext.MarkRunFailed(
        $"Missing {releaseCommitObjectsDict.Keys.Count} objects compared to the release commit."
      );

      foreach (var missingObject in releaseCommitObjectsDict)
      {
        Console.WriteLine(
          $"Missing object info: id( {missingObject.Value.id} ), applicationId( {missingObject.Key} ), type( {missingObject.Value.speckle_type} )"
        );
      }
    }
    // mark run as succeeded, noting any changed objects and added objects
    else
    {
      automationContext.MarkRunSuccess(
        $"Run passed with {addedCount} new objects, {modifiedCount} objects, and {unchangedCount} unchanged objects."
      );
    }
  }
}

using Objects;
using Speckle.Automate.Sdk;
using Speckle.Automate.Sdk.Schema;
using Speckle.Core.Api;
using Speckle.Core.Credentials;
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

    var tolerance = functionInputs.Tolerance;

    Console.WriteLine($"Comparing {testBranchName} against {releaseBranchName}");

    // get the test and release commits
    Console.WriteLine("Receiving test version");
    Base? testingCommitObject = await automationContext.ReceiveVersion();
    Console.WriteLine("Received test version: " + testingCommitObject);
    Console.WriteLine("Receiving release version");
    ServerTransport serverTransport = new ServerTransport(
      automationContext.SpeckleClient.Account,
      automationContext.AutomationRunData.ProjectId
    );
    Base? releaseCommitObject = await Operations
      .Receive(
        (
          await automationContext.SpeckleClient
            .CommitGet(automationContext.AutomationRunData.ProjectId, releaseCommit.id)
            .ConfigureAwait(continueOnCapturedContext: false)
        ).referencedObject,
        serverTransport,
        new MemoryTransport()
      )
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
      if (
        releaseObject.applicationId != null
        && !releaseCommitObjectsDict.ContainsKey(releaseObject.applicationId)
      )
      {
        releaseCommitObjectsDict.Add(releaseObject.applicationId, releaseObject);
      }
    }

    Console.WriteLine(
      $"Found {releaseCommitObjects.Count()} objects in release version"
    );
    Console.WriteLine($"Found {testCommitObjects.Count()} objects in release version");

    // compare objects
    int unchangedCount = 0;
    var addedList = new List<Tuple<string, string>>();
    var modifiedList = new List<Tuple<string, string, string>>();
    foreach (Base testObject in testCommitObjects)
    {
      if (
        testObject.applicationId != null
        && releaseCommitObjectsDict.ContainsKey(testObject.applicationId)
      )
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
          var diffDictionary = new Dictionary<string, string>();
          Dictionary<string, object?> releaseObjectPropDict =
            releaseObject.GetMembers();
          Dictionary<string, object?> testObjectPropDict = testObject.GetMembers();
          foreach (var entry in testObjectPropDict)
          {
            if (releaseObjectPropDict.ContainsKey(entry.Key))
            {
              try
              {
                bool changed = !Equals(entry.Value, releaseObjectPropDict[entry.Key]);
                if (changed)
                {
                  object? releaseValue = releaseObjectPropDict[entry.Key];
                  object? testValue = entry.Value;
                  string diff = $"Property ({entry.Key}) changed";
                  if (
                    releaseValue is not null
                    && testValue is not null
                    && !releaseValue.GetType().Equals(testValue.GetType())
                  )
                  {
                    diff +=
                      $" from ({releaseObjectPropDict[entry.Key]}) to ({entry.Value})";
                  }

                  if (!diffDictionary.ContainsKey(entry.Key))
                  {
                    diffDictionary.Add(entry.Key, diff);
                  }
                }
              }
              catch { }
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
              sb.AppendLine($"{entry.Key}: {entry.Value}. ");
            }

            modifiedList.Add(
              new Tuple<string, string, string>(
                testObject.id,
                testObject.speckle_type,
                sb.ToString()
              )
            );
            //automationContext.AttachWarningToObjects( MODIFIED, new List<string>() { testObject.id }, sb.ToString());
          }
        }

        releaseCommitObjectsDict.Remove(testObject.applicationId);
      }
      else
      {
        // we're skipping objects without an applicationId for now, since we're doing so in the release commit
        if (!string.IsNullOrEmpty(testObject.applicationId))
        {
          //automationContext.AttachInfoToObjects(ADDED, new List<string>() { testObject.id });
          addedList.Add(
            new Tuple<string, string>(testObject.id, testObject.speckle_type)
          );
        }
      }
    }

    // if there are any remaining release commit objects, this indicates missing objects in the test run.
    var deletedList = new List<Tuple<string, string>>();
    foreach (var entry in releaseCommitObjectsDict)
    {
      deletedList.Add(new Tuple<string, string>(entry.Key, entry.Value.speckle_type));
    }

    // mark run failed if there are any added, modified, or deleted objects and report
    if (addedList.Count + deletedList.Count + modifiedList.Count > 0)
    {
      automationContext.MarkRunFailed(
        $"Run failed due to {addedList.Count} ADDED, {modifiedList.Count} MODIFIED, and {deletedList.Count} DELETED objects compared to the release commit."
      );

      addedList.ForEach(
        o => Console.WriteLine($"ADDED object: id( {o.Item1} ), type( {o.Item2} )")
      );
      deletedList.ForEach(
        o => Console.WriteLine($"DELETED object: id( {o.Item1} ), type( {o.Item2} )")
      );
      modifiedList.ForEach(
        o =>
          Console.WriteLine(
            $"MODIFIED object: id( {o.Item1} ), type( {o.Item2} ), changed props:( {o.Item3} )"
          )
      );
    }
    else
    {
      automationContext.MarkRunSuccess(
        $"Run passed with {unchangedCount} unchanged objects."
      );
    }
  }

  public static bool Equals<T>(T a, T b)
  {
    switch (a)
    {
      case Base o:
        return b is Base bBase ? o.id == bBase.id : false;
      case List<object> aList:
        if (b is List<object> bList && aList.Count == bList.Count)
        {
          for (int i = 0; i < aList.Count; i++)
          {
            if (!Equals(aList[i], bList[i]))
            {
              return false;
            }
          }
          return true;
        }
        return false;
      case Dictionary<string, object> aDictionary:
        if (
          b is Dictionary<string, object> bDictionary
          && aDictionary.Count == bDictionary.Count
        )
        {
          foreach (var entry in aDictionary)
          {
            if (
              !bDictionary.ContainsKey(entry.Key)
              || !Equals(entry.Value, bDictionary[entry.Key])
            )
            {
              return false;
            }
          }
          return true;
        }
        return false;
      default:
        return EqualityComparer<T>.Default.Equals(a, b);
    }
  }
}

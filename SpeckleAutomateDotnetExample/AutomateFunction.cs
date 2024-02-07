using Objects;
using Speckle.Automate.Sdk;
using Speckle.Core.Api;
using Speckle.Core.Models;
using SpeckleAutomateDotnetExample;

public static class AutomateFunction
{
  public static string ADDED = "ADDED";
  public static string MODIFIED = "MODIFIED";
  public static string DELETED = "DELETED";
  public static string UNCHANGED = "UNCHANGED";

  public static async Task Run(
    AutomationContext automationContext,
    FunctionInputs functionInputs
  )
  {
    Console.WriteLine("Starting execution");
    _ = typeof(ObjectsKit).Assembly; // INFO: Force objects kit to initialize

    // get the testing and release branches
    string testBranchName = automationContext.AutomationRunData.BranchName;
    string releaseBranchName = functionInputs.DiffBranch;
    Console.WriteLine($"Comparing {testBranchName} against {releaseBranchName}");
    Branch? releaseBranch = await automationContext.SpeckleClient
      .BranchGet(automationContext.AutomationRunData.ProjectId, releaseBranchName, 1)
      .ConfigureAwait(false);
    if (releaseBranch is null)
    {
      throw new Exception("Release branch was null");
    }

    // get the release branch latest commit
    Commit releaseCommit = releaseBranch.commits.items.First();
    if (releaseCommit is null)
    {
      throw new Exception("Diff branch has no commits");
    }

    // get the test and release commit base
    Console.WriteLine("Receiving test version");
    Base testingCommitObject = await automationContext.ReceiveVersion();
    Console.WriteLine("Received test version: " + testingCommitObject);
    Console.WriteLine("Receiving release version");
    Base releaseCommitObject = await Utils.RecieveVersionAsync(
      releaseCommit.id,
      automationContext
    );
    Console.WriteLine("Received release version: " + releaseCommitObject);

    // Create dictionaries by appId (or speckle id if no appId exists) for the release and testing commits
    // note that it is possible for multiple objects to have the same app id
    // (eg mapped schema objects have the same id as their parent geometry)
    // and it is also possible for multiple objects (with no app id) to have the same speckle id
    // (eg the same mesh sent from gh multiple times)
    Dictionary<string, List<Base>> releaseCommitAppIdDict = new();
    Dictionary<string, List<Base>> releaseCommitSpeckleIdDict = new();
    Dictionary<string, List<Base>> testingCommitAppIdDict = new();
    Dictionary<string, List<Base>> testingCommitSpeckleIdDict = new();
    Utils.CreateDictionaryFromBaseById(
      releaseCommitObject,
      out releaseCommitAppIdDict,
      out releaseCommitSpeckleIdDict,
      out int releaseObjectCount
    );
    Console.WriteLine(
      $"Found {releaseObjectCount} objects in RELEASE with {releaseCommitAppIdDict.Count} unique applicationIds and {releaseCommitSpeckleIdDict.Count} unique speckle ids (for objects with no application id)."
    );
    Utils.CreateDictionaryFromBaseById(
      testingCommitObject,
      out testingCommitAppIdDict,
      out testingCommitSpeckleIdDict,
      out int testingObjectCount
    );
    Console.WriteLine(
      $"Found {testingObjectCount} objects in TESTING with {testingCommitAppIdDict.Count} unique applicationIds and {testingCommitSpeckleIdDict.Count} unique speckle ids (for objects with no application id)."
    );

    // COMPARE COMMIT OBJECTS WITH APPLICATION IDS
    // and store in hash lists where each object is (id, appId, type), and for modified, and additional string of property changes.
    HashSet<Tuple<string, string?, string>> deletedAppIdObjects = new();
    HashSet<Tuple<string, string?, string>> unchangedAppIdObjects = new();
    HashSet<Tuple<string, string?, string>> addedAppIdObjects = new();
    HashSet<Tuple<string, string?, string, string>> modifiedAppIdObjects = new();

    // first find deleted objects in the testing commit and remove their keys from the release commit dict
    foreach (string releaseAppId in releaseCommitAppIdDict.Keys)
    {
      if (!testingCommitAppIdDict.ContainsKey(releaseAppId))
      {
        releaseCommitAppIdDict[releaseAppId].ForEach(
          o =>
            deletedAppIdObjects.Add(
              new Tuple<string, string?, string>(o.id, releaseAppId, o.speckle_type)
            )
        );
        releaseCommitAppIdDict.Remove(releaseAppId);
      }
    }

    // then find unchanged, added, and modified objects by iterating through testing commit app ids
    foreach (string testingAppId in testingCommitAppIdDict.Keys)
    {
      List<Base> testObjects = testingCommitAppIdDict[testingAppId];

      // test for added objects
      if (!releaseCommitAppIdDict.ContainsKey(testingAppId))
      {
        testObjects.ForEach(
          o =>
            addedAppIdObjects.Add(
              new Tuple<string, string?, string>(o.id, testingAppId, o.speckle_type)
            )
        );
      }
      else
      {
        List<Base> releaseObjects = releaseCommitAppIdDict[testingAppId];

        // test for unchanged objects
        // by filtering the testing and release objects with matching speckle ids
        Utils
          .FilterListsBySpeckleIdMatch(testObjects, releaseObjects)
          .ForEach(o => unchangedAppIdObjects.Add(o));

        // for remaining objects, determine deleted objects
        // and then compare them in order (assume modified) and handle leftovers (added)
        // this is imperfect, as there's a chance we are not comparing the correct objects.
        if (releaseObjects.Count > testObjects.Count)
        {
          for (int i = releaseObjects.Count - 1; i >= testObjects.Count; i--)
          {
            deletedAppIdObjects.Add(
              new Tuple<string, string?, string>(
                releaseObjects[i].id,
                testingAppId,
                releaseObjects[i].speckle_type
              )
            );
            releaseObjects.RemoveAt(i);
          }
        }

        for (int i = 0; i < testObjects.Count; i++)
        {
          Base testObject = testObjects[i];

          if (i < releaseObjectCount)
          {
            Base releaseObject = releaseObjects[i];

            // compare object properties to determine changes
            List<string> addedProps = new();
            List<string> deletedProps = new();
            List<Tuple<string, string>> modifiedProps = new();
            Utils.CompareBaseProperties(
              testObject,
              releaseObject,
              out addedProps,
              out deletedProps,
              out modifiedProps
            );
            var sb = new System.Text.StringBuilder();
            addedProps.ForEach(s => sb.AppendLine($"{ADDED} prop ({s})."));
            deletedProps.ForEach(s => sb.AppendLine($"{DELETED} prop ({s})."));
            modifiedProps.ForEach(
              t => sb.AppendLine($"{MODIFIED} ({t.Item2}) of prop ({t.Item1})")
            );
            modifiedAppIdObjects.Add(
              new Tuple<string, string?, string, string>(
                testObject.id,
                testObject.applicationId,
                testObject.speckle_type,
                sb.ToString()
              )
            );
          }
          // remaining test objects are considered added
          else
          {
            addedAppIdObjects.Add(
              new Tuple<string, string?, string>(
                testObject.id,
                testingAppId,
                testObject.speckle_type
              )
            );
          }
        }
      }
    }

    // COMPARE COMMIT OBJECTS WITHOUT APPLICATION IDS USING SPECKLE IDS
    // since we only have 1 parameter of comparison, we can rule out any matching speckle ids as unchanged.
    // for all other objects, mark as modified, and indicate quantity of any added or deleted objects
    // store modified in hash lists of (id, type)
    HashSet<Tuple<string, string?, string>> unchangedSpeckleIdObjects = new();
    HashSet<Tuple<string, string>> changedSpeckleIdObjects = new();

    // first filter out matching speckle ids
    List<Base> flattenedTestingSpeckleIdDict = testingCommitSpeckleIdDict.Values
      .SelectMany(o => o)
      .ToList();
    List<Base> flattenedReleaseSpeckleIdDict = releaseCommitSpeckleIdDict.Values
      .SelectMany(o => o)
      .ToList();
    Utils
      .FilterListsBySpeckleIdMatch(
        flattenedTestingSpeckleIdDict,
        flattenedReleaseSpeckleIdDict
      )
      .ForEach(o => unchangedSpeckleIdObjects.Add(o));

    // then store all remaining testing objects as changed
    flattenedTestingSpeckleIdDict.ForEach(
      o => changedSpeckleIdObjects.Add(new Tuple<string, string>(o.id, o.speckle_type))
    );

    // calculate count difference
    int speckleIdObjectCountDifference =
      flattenedTestingSpeckleIdDict.Count - flattenedReleaseSpeckleIdDict.Count;

    // REPORT ALL DIFF RESULTS FOR APP IDS AND SPECKLE IDS
    // mark run succeeded if there are no added, modified, or deleted app id objects, and no changed speckle id objects
    // mark run failed otherwise
    if (
      addedAppIdObjects.Count
        + deletedAppIdObjects.Count
        + modifiedAppIdObjects.Count
        + changedSpeckleIdObjects.Count
      == 0
    )
    {
      automationContext.MarkRunSuccess($"Run passed with no changes to objects.");
    }
    else
    {
      automationContext.MarkRunFailed(
        $"Run failed due to: {addedAppIdObjects.Count} {ADDED}, {modifiedAppIdObjects.Count} {MODIFIED}, and {deletedAppIdObjects.Count} {DELETED} objects WITH APP IDS, and {(speckleIdObjectCountDifference > 0 ? $"{speckleIdObjectCountDifference} {ADDED}" : $"{Math.Abs(speckleIdObjectCountDifference)} {DELETED}")} and {changedSpeckleIdObjects.Count} CHANGED objects WITHOUT APP IDS compared to the release commit. "
      );

      foreach (var added in addedAppIdObjects)
      {
        Console.WriteLine(
          $"{ADDED} {added.Item3} object: id( {added.Item1} ), appId: {added.Item2}"
        );
      }

      if (addedAppIdObjects.Count > 0)
      {
        automationContext.AttachErrorToObjects(
          "ADDED",
          addedAppIdObjects.Select(o => o.Item1),
          "added objects with an application Id"
        );
      }

      foreach (var deleted in deletedAppIdObjects)
      {
        Console.WriteLine(
          $"{DELETED} {deleted.Item3} object: id( {deleted.Item1} ), appId: {deleted.Item2}"
        );
      }

      foreach (var modified in modifiedAppIdObjects)
      {
        Console.WriteLine(
          $"{MODIFIED} {modified.Item3} object: id( {modified.Item1} ), appId: {modified.Item2}, category: {modified.Item4}"
        );
      }

      if (modifiedAppIdObjects.Count > 0)
      {
        automationContext.AttachErrorToObjects(
          "MODIFIED",
          modifiedAppIdObjects.Select(o => o.Item1),
          "modified objects with an application Id"
        );
      }

      foreach (var changed in changedSpeckleIdObjects)
      {
        Console.WriteLine($"CHANGED {changed.Item2} object: id( {changed.Item1} )");
      }

      if (changedSpeckleIdObjects.Count > 0)
      {
        automationContext.AttachErrorToObjects(
          "CHANGED",
          changedSpeckleIdObjects.Select(o => o.Item1),
          "changed objects with no application Id"
        );
      }
    }
  }
}

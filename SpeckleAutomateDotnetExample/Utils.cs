using Speckle.Automate.Sdk;
using Speckle.Automate.Sdk.Schema;
using Speckle.Core.Api;
using Speckle.Core.Models;
using Speckle.Core.Models.Extensions;
using Speckle.Core.Transports;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SpeckleAutomateDotnetExample
{
  internal class Utils
  {
    /// <summary>
    /// Receives a commit object from the same project and account as the <paramref name="context"/>
    /// </summary>
    /// <param name="commitId"> The id of the commit to receive</param>
    /// <param name="context">The Automation context</param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public static async Task<Base> RecieveVersionAsync(
      string commitId,
      AutomationContext context
    )
    {
      ServerTransport serverTransport = new ServerTransport(
        context.SpeckleClient.Account,
        context.AutomationRunData.ProjectId
      );

      Base? receivedCommitObject = await Operations
        .Receive(
          (
            await context.SpeckleClient
              .CommitGet(context.AutomationRunData.ProjectId, commitId)
              .ConfigureAwait(continueOnCapturedContext: false)
          ).referencedObject,
          serverTransport,
          new MemoryTransport()
        )
        .ConfigureAwait(continueOnCapturedContext: false);

      if (receivedCommitObject == null)
      {
        throw new Exception("Commit root object was null");
      }

      return receivedCommitObject;
    }

    /// <summary>
    /// Creates Dictionaries from a commit Base, using applicationId if available or speckle id if no applicationId exists.
    /// </summary>
    /// <param name="commit">The commit Base to create dictionaries from</param>
    /// <param name="appIdDict">The dictionary of commit objects sorted by applicationId</param>
    /// <param name="speckleIdDict">The dictionary of commit objects without applicationIds, sorted by speckle id</param>
    public static void CreateDictionaryFromBaseById(
      Base commit,
      out Dictionary<string, List<Base>> appIdDict,
      out Dictionary<string, List<Base>> speckleIdDict,
      out int objectCount
    )
    {
      IEnumerable<Base> commitObjects = commit.Flatten();
      objectCount = commitObjects.Count();

      appIdDict = new Dictionary<string, List<Base>>();
      speckleIdDict = new Dictionary<string, List<Base>>();
      foreach (var commitObject in commitObjects)
      {
        if (!string.IsNullOrWhiteSpace(commitObject.applicationId))
        {
          if (appIdDict.ContainsKey(commitObject.applicationId))
          {
            appIdDict[commitObject.applicationId].Add(commitObject);
          }
          else
          {
            appIdDict.Add(
              commitObject.applicationId,
              new List<Base>() { commitObject }
            );
          }
        }
        else if (!string.IsNullOrWhiteSpace(commitObject.id))
        {
          if (speckleIdDict.ContainsKey(commitObject.id))
          {
            speckleIdDict[commitObject.id].Add(commitObject);
          }
          else
          {
            speckleIdDict.Add(commitObject.id, new List<Base>() { commitObject });
          }
        }
      }
    }

    /// <summary>
    /// Filters two lists by removing all objects from <paramref name="setA"/> with a matching speckle id in <paramref name="setB"/>
    /// </summary>
    /// <param name="setA">The list to filter</param>
    /// <param name="setB">The list to filter against</param>
    /// <returns>A list of matches found in <paramref name="setA"/></returns>
    public static List<Tuple<string, string?, string>> FilterListsBySpeckleIdMatch(
      List<Base> setA,
      List<Base> setB
    )
    {
      var matches = new List<Tuple<string, string?, string>>();

      for (int i = setA.Count - 1; i >= 0; i--)
      {
        var testObject = setA[i];
        for (int j = setB.Count - 1; j >= 0; j--)
        {
          var releaseObject = setB[j];

          // if a match was found, remove from both lists and add to matches
          if (testObject.id == releaseObject.id)
          {
            matches.Add(
              new Tuple<string, string?, string>(
                testObject.id,
                testObject.applicationId,
                testObject.speckle_type
              )
            );
            setA.RemoveAt(i);
            setB.RemoveAt(j);
          }
        }
      }
      return matches;
    }

    /// <summary>
    /// Compares the properties of <paramref name="a"/> against <paramref name="b"/>
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <param name="addedProps">Properties on <paramref name="a"/> that do not exist on <paramref name="b"/></param>
    /// <param name="deletedProps">Properties on <paramref name="b"/> that do not exist on <paramref name="a"/></param>
    /// <param name="modifiedProps">Properties on <paramref name="a"/> that have a different value on <paramref name="b"/>. The second string is the category of modification (eg count/primitive/base)</param>
    public static void CompareBaseProperties(
      Base a,
      Base b,
      out List<string> addedProps,
      out List<string> deletedProps,
      out List<Tuple<string, string>> modifiedProps
    )
    {
      addedProps = new List<string>();
      deletedProps = new List<string>();
      modifiedProps = new List<Tuple<string, string>>();
      Dictionary<string, object?> bObjectPropDict = b.GetMembers();
      Dictionary<string, object?> aObjectPropDict = a.GetMembers();
      foreach (KeyValuePair<string, object?> entry in aObjectPropDict)
      {
        if (bObjectPropDict.ContainsKey(entry.Key))
        {
          bool changed = !IsEqual(
            entry.Value,
            bObjectPropDict[entry.Key],
            out string category
          );
          if (changed)
          {
            modifiedProps.Add(new Tuple<string, string>(entry.Key, category));
          }

          bObjectPropDict.Remove(entry.Key);
        }
        else
        {
          addedProps.Add(entry.Key);
        }
      }

      // check if there are any props left on the b - these were missing in the a
      foreach (var entry in bObjectPropDict)
      {
        deletedProps.Add(entry.Key);
      }
    }

    /// <summary>
    /// Determines equality between two objects
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <param name="category">The category of the modification, if objects are not equal</param>
    /// <returns></returns>
    public static bool IsEqual<T>(T a, T b, out string category)
    {
      category = "Value";
      switch (a)
      {
        case Base aBase:
          if (b is Base bBase && bBase.speckle_type == aBase.speckle_type)
          {
            return aBase.id == bBase.id;
          }
          else
          {
            category = "Type";
            return false;
          }

        case List<object> aList:
          if (b is List<object> bList)
          {
            if (aList.Count != bList.Count)
            {
              category = "Count";
              return false;
            }

            for (int i = 0; i < aList.Count; i++)
            {
              if (!Equals(aList[i], bList[i]))
              {
                return false;
              }
            }
            return true;
          }
          else
          {
            category = "Type";
            return false;
          }

        case Dictionary<string, object> aDictionary:
          if (b is Dictionary<string, object> bDictionary)
          {
            if (aDictionary.Count != bDictionary.Count)
            {
              category = "Count";
              return false;
            }
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
          else
          {
            category = "Type";
            return false;
          }
        default:
          try
          {
            return EqualityComparer<T>.Default.Equals(a, b);
          }
          catch
          {
            return false;
          }
      }
    }
  }
}

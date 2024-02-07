using System.Diagnostics.CodeAnalysis;
using GraphQL;
using Speckle.Core.Api;
using Speckle.Core.Models;

namespace TestAutomateFunction;

public static class TestAutomateUtils
{
  [SuppressMessage("Security", "CA5394:Do not use insecure randomness")]
  public static string RandomString(int length)
  {
    Random rand = new();
    const string pool = "abcdefghijklmnopqrstuvwxyz0123456789";
    var chars = Enumerable
      .Range(0, length)
      .Select(_ => pool[rand.Next(0, pool.Length)]);
    return new string(chars.ToArray());
  }

  /// <summary>
  /// The test object to compare against the release object
  /// </summary>
  /// <returns></returns>
  /// <remarks>Contains 3 children objects to capture the Added, Deleted, Modified, and Unchanged categories</remarks>
  public static Base TestObject()
  {
    Base unchanged = new() { ["unchangedProp"] = true };
    Base added = new() { ["addedProp"] = "added" };
    Base modified = new() { ["modifiedProp"] = 1 };
    Base testObject = new();
    testObject["unchanged"] = unchanged;
    testObject["added"] = added;
    testObject["modified"] = modified;
    return testObject;
  }

  /// <summary>
  /// The release object to compare the test object against
  /// </summary>
  /// <returns>Contains 3 children objects to capture the Added, Deleted, Modified, and Unchanged categories</returns>
  public static Base ReleaseObject()
  {
    Base unchanged = new() { ["unchangedProp"] = true };
    Base deleted = new() { ["deletedProp"] = "deleted" };
    Base modified = new() { ["modifiedProp"] = 0.5 };
    Base releaseObject = new();
    releaseObject["unchanged"] = unchanged;
    releaseObject["deleted"] = deleted;
    releaseObject["modified"] = modified;
    return releaseObject;
  }

  public static async Task RegisterNewAutomation(
    string projectId,
    string modelId,
    Client speckleClient,
    string automationId,
    string automationName,
    string automationRevisionId
  )
  {
    GraphQLRequest query =
      new(
        query: """
               mutation CreateAutomation(
                   $projectId: String!
                   $modelId: String!
                   $automationName: String!
                   $automationId: String!
                   $automationRevisionId: String!
               ) {
                       automationMutations {
                           create(
                               input: {
                                   projectId: $projectId
                                   modelId: $modelId
                                   automationName: $automationName
                                   automationId: $automationId
                                   automationRevisionId: $automationRevisionId
                               }
                           )
                       }
                   }
               """,
        variables: new
        {
          projectId,
          modelId,
          automationName,
          automationId,
          automationRevisionId,
        }
      );

    await speckleClient.ExecuteGraphQLRequest<object>(query);
  }
}

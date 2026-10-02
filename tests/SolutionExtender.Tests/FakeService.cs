using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using SolutionExtender;
using SolutionExtender.Tool;
using Xunit;

namespace SolutionExtender.Tests;

internal sealed class FakeService : IOrganizationService
{
    public List<string> Calls { get; } = [];

    public bool FailDelete
    {
        get; set;
    }

    public Func<QueryExpression, EntityCollection> Query { get; init; } = _ => new();

    public Func<string, Guid, Entity>? RetrieveHandler
    {
        get; init;
    }

    public Entity RetrieveEntity { get; init; } = new("workflow", Guid.NewGuid());

    public EntityCollection RetrieveMultiple(QueryBase query) => Query((QueryExpression)query);

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => RetrieveHandler?.Invoke(entityName, id) ?? RetrieveEntity;

    public void Delete(string entityName, Guid id)
    {
        Calls.Add("delete:" + entityName);
        if (FailDelete)
        {
            throw new InvalidOperationException("Simulated dependency failure.");
        }
    }

    public OrganizationResponse Execute(OrganizationRequest request)
    {
        Calls.Add(request.RequestName);
        return new();
    }

    public Guid Create(Entity entity) => throw new NotSupportedException();

    public void Update(Entity entity) => throw new NotSupportedException();

    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotSupportedException();

    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotSupportedException();
}

using FluentValidation;
using Projector.Application.Auth;
using Projector.Contracts.Common;
using Projector.Contracts.Resources;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;
using Projector.Domain.Pagination;
using Projector.Domain.Resources;

namespace Projector.Application.Resources;

public sealed class ListResourcesValidator : AbstractValidator<ListResourcesRequest>
{
    public ListResourcesValidator()
    {
        RuleFor(x => x.MaxRows).InclusiveBetween(1, 100);
        RuleFor(x => x.Query).MaximumLength(255).When(x => x.Query is not null);
    }
}

public sealed class GetResourceValidator : AbstractValidator<GetResourceRequest>
{
    public GetResourceValidator()
    {
        RuleFor(x => x.Id).NotEmpty().MaximumLength(255);
    }
}

public sealed class ResourceService
{
    private readonly ProjectorConnectionService _connections;
    private readonly IProjectorResourceClient _resources;
    private readonly IValidator<ListResourcesRequest> _listValidator;
    private readonly IValidator<GetResourceRequest> _getValidator;

    public ResourceService(
        ProjectorConnectionService connections,
        IProjectorResourceClient resources,
        IValidator<ListResourcesRequest> listValidator,
        IValidator<GetResourceRequest> getValidator)
    {
        _connections = connections;
        _resources = resources;
        _listValidator = listValidator;
        _getValidator = getValidator;
    }

    public async Task<ListResourcesResponse> ListAsync(
        string connectionId,
        ListResourcesRequest request,
        CancellationToken cancellationToken = default)
    {
        await _listValidator.ValidateAndThrowAsync(request, cancellationToken);
        var connection = await _connections.RequireConnectionAsync(
            connectionId,
            ProjectorScopes.BrowseResources,
            cancellationToken);

        ResourceListResult listed;
        try
        {
            listed = await _resources.ListResourcesAsync(
                connection,
                request.Query,
                request.IncludeInactive,
                request.MaxRows,
                cancellationToken);
        }
        catch (ProjectorApiException ex) when (IsAuthFailure(ex))
        {
            connection = await _connections.RefreshConnectionAsync(connection, cancellationToken);
            listed = await _resources.ListResourcesAsync(
                connection,
                request.Query,
                request.IncludeInactive,
                request.MaxRows,
                cancellationToken);
        }

        var dtos = listed.Resources.Select(r => new ResourceSummaryDto(
            r.ResourceUid,
            r.ResourceReferenceSystemId,
            r.DisplayName,
            r.EmailAddress,
            r.Inactive)).ToList();

        var inactiveScope = request.IncludeInactive
            ? "active and inactive resources"
            : "active resources only";
        var queryScope = string.IsNullOrWhiteSpace(request.Query)
            ? "no server query"
            : $"server query '{request.Query.Trim()}'";
        var page = PageResultFactory.Create(
            dtos,
            maxRows: request.MaxRows,
            serverTruncated: listed.ServerTruncated,
            searchedScope: $"{inactiveScope}; asked Projector for up to {request.MaxRows} resources using {queryScope}",
            truncationReason: $"Projector hit its {request.MaxRows}-resource row cap and returned a partial list.",
            truncationSuggestion:
                "Raise max_rows or narrow query to bring the full match set under the Projector row cap.");

        return new ListResourcesResponse(
            page.Items,
            page.Count,
            page.HasMore,
            SearchCoverageDto.From(page.SearchCoverage),
            page.Items.Select(r => new ResourceLinkDto(
                $"projector://resources/{r.ResourceReferenceSystemId}",
                r.DisplayName)).ToList(),
            page.NextOffset);
    }

    public async Task<GetResourceResponse> GetAsync(
        string connectionId,
        GetResourceRequest request,
        CancellationToken cancellationToken = default)
    {
        await _getValidator.ValidateAndThrowAsync(request, cancellationToken);
        var connection = await _connections.RequireConnectionAsync(
            connectionId,
            ProjectorScopes.BrowseResources,
            cancellationToken);

        ResourceDetail? detail;
        try
        {
            detail = await GetByIdOrEmailAsync(connection, request, cancellationToken);
        }
        catch (ProjectorApiException ex) when (IsAuthFailure(ex))
        {
            connection = await _connections.RefreshConnectionAsync(connection, cancellationToken);
            detail = await GetByIdOrEmailAsync(connection, request, cancellationToken);
        }

        if (detail is null)
        {
            throw new ProjectorApiException(
                $"Resource '{request.Id}' was not found.",
                "AtLeastOneItemNotFound");
        }

        var mapped = Map(detail, request.IncludeHistory, request.IncludeUdfs);
        var resourceId = !string.IsNullOrWhiteSpace(mapped.ResourceReferenceSystemId)
            ? mapped.ResourceReferenceSystemId!
            : (!string.IsNullOrWhiteSpace(mapped.ResourceUid) ? mapped.ResourceUid! : request.Id);

        return new GetResourceResponse(
            $"projector://resources/{resourceId}",
            mapped,
            [
                new ResourceLinkDto($"projector://resources/{resourceId}", mapped.DisplayName),
                new ResourceLinkDto($"projector://resources/{resourceId}/history", "history"),
                new ResourceLinkDto($"projector://resources/{resourceId}/udfs", "udfs")
            ]);
    }

    /// <summary>
    /// PwsGetResource cannot take an email, so an email is resolved through the resource list
    /// (<see cref="ResourceEmailResolver"/>) and the detail is then loaded by ResourceReferenceSystemId.
    /// </summary>
    private async Task<ResourceDetail?> GetByIdOrEmailAsync(
        ProjectorConnection connection,
        GetResourceRequest request,
        CancellationToken cancellationToken)
    {
        var id = request.Id.Trim();
        if (ResourceEmailResolver.LooksLikeEmail(id))
        {
            var match = await ResourceEmailResolver.FindAsync(_resources, connection, id, cancellationToken);
            if (string.IsNullOrWhiteSpace(match?.ResourceReferenceSystemId))
            {
                throw new ProjectorApiException(
                    $"No resource has the email '{id}'. Try full_name or list_resources.",
                    "AtLeastOneItemNotFound");
            }

            id = match.ResourceReferenceSystemId;
        }

        return await _resources.GetResourceAsync(
            connection, id, request.IncludeHistory, request.IncludeUdfs, cancellationToken);
    }

    private static bool IsAuthFailure(ProjectorApiException ex) =>
        string.Equals(ex.ErrorCode, "InvalidSessionTicket", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ex.ErrorCode, "AccessPermissionDenied", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ex.ErrorCode, "ViewPermissionDenied", StringComparison.OrdinalIgnoreCase)
        || (ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase));

    internal static ResourceDetailDto Map(
        ResourceDetail detail,
        bool includeHistory,
        bool includeUdfs) =>
        new(
            detail.ResourceUid,
            detail.ResourceReferenceSystemId,
            detail.DisplayName,
            detail.FirstName,
            detail.LastName,
            detail.EmailAddress,
            detail.Inactive,
            detail.ManagerDisplayName,
            detail.ManagerEmail,
            detail.TimecardApproverDisplayName,
            detail.ExpenseApproverDisplayName,
            detail.LocationName,
            detail.CostCenterName,
            detail.DepartmentName,
            detail.TitleName,
            detail.ResourceTypeName,
            includeUdfs
                ? detail.Udfs.Select(u => new ResourceUdfDto(u.Name, u.Value)).ToList()
                : null,
            includeHistory
                ? detail.History.Select(h => new ResourceHistoryDto(
                    h.Index,
                    h.IsActive,
                    h.EffectiveDate,
                    h.LocationName,
                    h.CostCenterName,
                    h.DepartmentName,
                    h.TitleName,
                    h.ResourceTypeName,
                    h.TrackMissingTime)).ToList()
                : null);
}

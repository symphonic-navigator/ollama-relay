using System.Security.Claims;
using MongoDB.Bson;
using MongoDB.Driver;
using OllamaRelay.Protocol;
using OllamaRelay.Relay.Data;
using OllamaRelay.Relay.Inference;
using OllamaRelay.Relay.Nodes;
using OllamaRelay.Relay.Security;

namespace OllamaRelay.Relay.Mgmt;

public static class AdminEndpoints
{
    public static void Map(IEndpointRouteBuilder app, RelayOptions options)
    {
        var g = app.MapGroup("/mgmt/admin").RequireAuthorization(Policies.Admin);
        MapNodes(g);
        MapPublications(g);
        MapUsers(g, options);
        MapGrants(g);
        MapUsage(g);
    }

    private static IResult Bad(string error) => Results.Json(new { error }, statusCode: 400);

    // ---------------------------------------------------------------- nodes

    private static void MapNodes(RouteGroupBuilder g)
    {
        g.MapGet("/nodes", async (MongoStore store, NodeHub hub, ActiveStreams active, Scheduler scheduler) =>
        {
            var nodes = await store.Nodes.Find(FilterDefinition<NodeDoc>.Empty).SortBy(n => n.Name).ToListAsync();
            var pubs = await store.Publications.Find(FilterDefinition<PublicationDoc>.Empty).ToListAsync();
            var streams = active.Snapshot();
            return Results.Ok(new
            {
                queueDepth = scheduler.Waiting,
                queued = streams.Where(s => s.Queued).Select(StreamView),
                nodes = nodes.Select(n =>
                {
                    var c = hub.Get(n.Id);
                    var hb = c?.LastHeartbeat;
                    var aliasOf = pubs.Where(p => p.NodeId == n.Id).ToLookup(p => p.UpstreamModel, p => p.Alias);
                    return new
                    {
                        id = n.Id.ToString(),
                        n.Name,
                        n.MaxConcurrency,
                        hasToken = n.TokenHash is not null,
                        n.TokenPrefix,
                        n.TokenIssuedAt,
                        online = c is not null,
                        connectedAt = c?.ConnectedAt,
                        lastHeartbeatAt = c?.LastHeartbeatAt,
                        lastSeenAt = n.LastSeenAt,
                        agentVersion = c?.AgentVersion ?? n.LastAgentVersion,
                        ollamaVersion = hb?.OllamaVersion ?? n.LastOllamaVersion,
                        ollamaReachable = hb?.OllamaReachable,
                        activeLeases = c?.ActiveLeases ?? 0,
                        host = hb?.Host,
                        loaded = (hb?.Loaded ?? []).Select(m => new
                        {
                            m.Name,
                            aliases = aliasOf[m.Name].ToList(),
                            m.Size,
                            m.SizeVram,
                            m.ExpiresAt,
                            m.ContextLength,
                        }),
                        streams = streams.Where(s => s.NodeId == n.Id && !s.Queued).Select(StreamView),
                        reportedModels = n.ReportedModels.Count,
                    };
                }),
            });
        });

        g.MapPost("/nodes", async (CreateNodeRequest req, ClaimsPrincipal principal, MongoStore store,
            AuditService audit) =>
        {
            var name = (req.Name ?? "").Trim();
            if (!AuthEndpoints.IsValidUsername(name))
            {
                return Bad("Node name must be 3 to 32 characters: letters, digits, dot, dash or underscore.");
            }

            var max = req.MaxConcurrency ?? 2;
            if (max is < 1 or > 64)
            {
                return Bad("Max concurrency must be between 1 and 64.");
            }

            var issued = SecretTokens.NewNodeToken();
            var node = new NodeDoc
            {
                Id = ObjectId.GenerateNewId(),
                Name = name,
                MaxConcurrency = max,
                TokenHash = issued.Hash,
                TokenPrefix = issued.Prefix,
                CreatedAt = DateTime.UtcNow,
                TokenIssuedAt = DateTime.UtcNow,
            };
            try
            {
                await store.Nodes.InsertOneAsync(node);
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                return Results.Json(new { error = "A node with this name exists." }, statusCode: 409);
            }

            await audit.WriteAsync(Actor.From(principal), "node.created", "node", node.Id.ToString(), node.Name);
            return Results.Ok(new { id = node.Id.ToString(), token = issued.Secret });
        });

        g.MapPatch("/nodes/{id}", async (string id, UpdateNodeRequest req, ClaimsPrincipal principal,
            MongoStore store, NodeHub hub, AuditService audit) =>
        {
            if (!Views.TryId(id, out var nodeId))
            {
                return Results.NotFound();
            }

            var update = new List<UpdateDefinition<NodeDoc>>();
            if (req.Name is not null)
            {
                if (!AuthEndpoints.IsValidUsername(req.Name.Trim()))
                {
                    return Bad("Node name must be 3 to 32 characters: letters, digits, dot, dash or underscore.");
                }

                update.Add(Builders<NodeDoc>.Update.Set(n => n.Name, req.Name.Trim()));
            }

            if (req.MaxConcurrency is { } max)
            {
                if (max is < 1 or > 64)
                {
                    return Bad("Max concurrency must be between 1 and 64.");
                }

                update.Add(Builders<NodeDoc>.Update.Set(n => n.MaxConcurrency, max));
            }

            if (update.Count == 0)
            {
                return Bad("Nothing to change.");
            }

            NodeDoc? node;
            try
            {
                node = await store.Nodes.FindOneAndUpdateAsync(n => n.Id == nodeId,
                    Builders<NodeDoc>.Update.Combine(update),
                    new FindOneAndUpdateOptions<NodeDoc> { ReturnDocument = ReturnDocument.After });
            }
            catch (MongoCommandException ex) when (ex.Code == 11000)
            {
                return Results.Json(new { error = "A node with this name exists." }, statusCode: 409);
            }

            if (node is null)
            {
                return Results.NotFound();
            }

            hub.UpdateSettings(node.Id, node.Name, node.MaxConcurrency);
            await audit.WriteAsync(Actor.From(principal), "node.updated", "node", id, node.Name);
            return Results.NoContent();
        });

        g.MapPost("/nodes/{id}/rotate", async (string id, ClaimsPrincipal principal, MongoStore store,
            NodeHub hub, AuditService audit) =>
        {
            if (!Views.TryId(id, out var nodeId))
            {
                return Results.NotFound();
            }

            var issued = SecretTokens.NewNodeToken();
            var node = await store.Nodes.FindOneAndUpdateAsync(n => n.Id == nodeId, Builders<NodeDoc>.Update
                .Set(n => n.TokenHash, issued.Hash)
                .Set(n => n.TokenPrefix, issued.Prefix)
                .Set(n => n.TokenIssuedAt, DateTime.UtcNow));
            if (node is null)
            {
                return Results.NotFound();
            }

            await hub.DisconnectAsync(nodeId, ProtocolConstants.CloseRevoked, "token rotated");
            await audit.WriteAsync(Actor.From(principal), "node.token_rotated", "node", id, node.Name);
            return Results.Ok(new { token = issued.Secret });
        });

        g.MapPost("/nodes/{id}/revoke", async (string id, ClaimsPrincipal principal, MongoStore store,
            NodeHub hub, AuditService audit) =>
        {
            if (!Views.TryId(id, out var nodeId))
            {
                return Results.NotFound();
            }

            var node = await store.Nodes.FindOneAndUpdateAsync(n => n.Id == nodeId, Builders<NodeDoc>.Update
                .Set(n => n.TokenHash, null)
                .Set(n => n.TokenPrefix, null));
            if (node is null)
            {
                return Results.NotFound();
            }

            await hub.DisconnectAsync(nodeId, ProtocolConstants.CloseRevoked, "token revoked");
            await audit.WriteAsync(Actor.From(principal), "node.token_revoked", "node", id, node.Name);
            return Results.NoContent();
        });

        g.MapDelete("/nodes/{id}", async (string id, ClaimsPrincipal principal, MongoStore store, NodeHub hub,
            AuditService audit) =>
        {
            if (!Views.TryId(id, out var nodeId))
            {
                return Results.NotFound();
            }

            var node = await store.Nodes.FindOneAndDeleteAsync(n => n.Id == nodeId);
            if (node is null)
            {
                return Results.NotFound();
            }

            await hub.DisconnectAsync(nodeId, ProtocolConstants.CloseRevoked, "node deleted");
            var pubIds = await store.Publications.Find(p => p.NodeId == nodeId).Project(p => p.Id).ToListAsync();
            await store.Grants.DeleteManyAsync(gr => pubIds.Contains(gr.PublicationId));
            await store.Publications.DeleteManyAsync(p => p.NodeId == nodeId);
            await audit.WriteAsync(Actor.From(principal), "node.deleted", "node", id, node.Name);
            return Results.NoContent();
        });

        g.MapGet("/nodes/{id}/models", async (string id, MongoStore store) =>
        {
            if (!Views.TryId(id, out var nodeId))
            {
                return Results.NotFound();
            }

            var node = await store.Nodes.Find(n => n.Id == nodeId).FirstOrDefaultAsync();
            if (node is null)
            {
                return Results.NotFound();
            }

            var pubs = await store.Publications.Find(p => p.NodeId == nodeId).ToListAsync();
            var reported = node.ReportedModels.Select(m => m.Name).ToHashSet();
            return Results.Ok(new
            {
                models = node.ReportedModels.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => new
                {
                    m.Name,
                    m.Size,
                    m.Family,
                    m.ParameterSize,
                    m.QuantizationLevel,
                    m.ModifiedAt,
                    publications = pubs.Where(p => p.UpstreamModel == m.Name)
                        .Select(p => new { id = p.Id.ToString(), p.Alias }),
                }),
                // Publications whose model the node no longer reports.
                orphaned = pubs.Where(p => !reported.Contains(p.UpstreamModel))
                    .Select(p => new { id = p.Id.ToString(), p.Alias, p.UpstreamModel }),
            });
        });
    }

    private static object StreamView(ActiveStream s) => new
    {
        id = s.Id,
        user = s.Username,
        s.Alias,
        s.Endpoint,
        node = s.NodeName,
        since = s.StartedAt,
        s.Queued,
    };

    // ---------------------------------------------------------------- publications

    private static void MapPublications(RouteGroupBuilder g)
    {
        g.MapGet("/publications", async (MongoStore store) =>
        {
            var pubs = await store.Publications.Find(FilterDefinition<PublicationDoc>.Empty).SortBy(p => p.Alias)
                .ToListAsync();
            var nodes = (await store.Nodes.Find(FilterDefinition<NodeDoc>.Empty).ToListAsync())
                .ToDictionary(n => n.Id, n => n.Name);
            return Results.Ok(pubs.Select(p => new
            {
                id = p.Id.ToString(),
                nodeId = p.NodeId.ToString(),
                node = nodes.GetValueOrDefault(p.NodeId, "?"),
                p.UpstreamModel,
                p.Alias,
                p.CreatedAt,
            }));
        });

        g.MapPost("/publications", async (CreatePublicationRequest req, ClaimsPrincipal principal,
            MongoStore store, AuditService audit) =>
        {
            if (!Views.TryId(req.NodeId, out var nodeId))
            {
                return Bad("Unknown node.");
            }

            var alias = (req.Alias ?? "").Trim();
            if (alias.EndsWith(":latest", StringComparison.Ordinal))
            {
                alias = alias[..^7];
            }

            if (!ModelNames.IsValidAlias(alias))
            {
                return Bad("Alias must match ^[a-z0-9][a-z0-9._-]{0,62}$ (lowercase, no colon).");
            }

            var node = await store.Nodes.Find(n => n.Id == nodeId).FirstOrDefaultAsync();
            if (node is null)
            {
                return Bad("Unknown node.");
            }

            var upstream = (req.UpstreamModel ?? "").Trim();
            if (node.ReportedModels.All(m => m.Name != upstream))
            {
                return Bad("The node does not report this model.");
            }

            var pub = new PublicationDoc
            {
                Id = ObjectId.GenerateNewId(),
                NodeId = nodeId,
                UpstreamModel = upstream,
                Alias = alias,
                CreatedAt = DateTime.UtcNow,
            };
            try
            {
                await store.Publications.InsertOneAsync(pub);
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                return Results.Json(new { error = "This alias is already published on this node." }, statusCode: 409);
            }

            await audit.WriteAsync(Actor.From(principal), "publication.created", "publication", pub.Id.ToString(),
                $"{alias} on {node.Name}");
            return Results.Ok(new { id = pub.Id.ToString() });
        });

        g.MapPatch("/publications/{id}", async (string id, UpdatePublicationRequest req, ClaimsPrincipal principal,
            MongoStore store, AuditService audit) =>
        {
            if (!Views.TryId(id, out var pubId))
            {
                return Results.NotFound();
            }

            var alias = (req.Alias ?? "").Trim();
            if (!ModelNames.IsValidAlias(alias))
            {
                return Bad("Alias must match ^[a-z0-9][a-z0-9._-]{0,62}$ (lowercase, no colon).");
            }

            try
            {
                var pub = await store.Publications.FindOneAndUpdateAsync(p => p.Id == pubId,
                    Builders<PublicationDoc>.Update.Set(p => p.Alias, alias));
                if (pub is null)
                {
                    return Results.NotFound();
                }

                await audit.WriteAsync(Actor.From(principal), "publication.renamed", "publication", id,
                    $"{pub.Alias} -> {alias}");
            }
            catch (MongoCommandException ex) when (ex.Code == 11000)
            {
                return Results.Json(new { error = "This alias is already published on this node." }, statusCode: 409);
            }

            return Results.NoContent();
        });

        g.MapDelete("/publications/{id}", async (string id, ClaimsPrincipal principal, MongoStore store,
            AuditService audit) =>
        {
            if (!Views.TryId(id, out var pubId))
            {
                return Results.NotFound();
            }

            var pub = await store.Publications.FindOneAndDeleteAsync(p => p.Id == pubId);
            if (pub is null)
            {
                return Results.NotFound();
            }

            await store.Grants.DeleteManyAsync(gr => gr.PublicationId == pubId);
            await audit.WriteAsync(Actor.From(principal), "publication.deleted", "publication", id, pub.Alias);
            return Results.NoContent();
        });
    }

    // ---------------------------------------------------------------- users and invites

    private static void MapUsers(RouteGroupBuilder g, RelayOptions options)
    {
        g.MapGet("/users", async (MongoStore store) =>
        {
            var users = await store.Users.Find(FilterDefinition<UserDoc>.Empty).SortBy(u => u.UsernameLower)
                .ToListAsync();
            var keyCounts = (await store.ApiKeys.Aggregate()
                    .Group(k => k.UserId, grp => new { UserId = grp.Key, Count = grp.Count() }).ToListAsync())
                .ToDictionary(x => x.UserId, x => x.Count);
            return Results.Ok(users.Select(u => new
            {
                id = u.Id.ToString(),
                u.Username,
                u.IsAdmin,
                u.Disabled,
                u.MustChangePassword,
                u.CreatedAt,
                u.LastLoginAt,
                apiKeys = keyCounts.GetValueOrDefault(u.Id),
            }));
        });

        g.MapPatch("/users/{id}", async (string id, UpdateUserRequest req, ClaimsPrincipal principal,
            MongoStore store, Sessions sessions, ActiveStreams active, AuditService audit) =>
        {
            if (!Views.TryId(id, out var userId))
            {
                return Results.NotFound();
            }

            if (userId == principal.UserId() && (req.Disabled == true || req.IsAdmin == false))
            {
                return Bad("You cannot disable yourself or remove your own admin role.");
            }

            var update = new List<UpdateDefinition<UserDoc>>();
            if (req.Disabled is { } disabled)
            {
                update.Add(Builders<UserDoc>.Update.Set(u => u.Disabled, disabled));
                if (disabled)
                {
                    update.Add(Builders<UserDoc>.Update.Inc(u => u.TokenVersion, 1));
                }
            }

            if (req.IsAdmin is { } admin)
            {
                update.Add(Builders<UserDoc>.Update.Set(u => u.IsAdmin, admin).Inc(u => u.TokenVersion, 1));
            }

            if (update.Count == 0)
            {
                return Bad("Nothing to change.");
            }

            var user = await store.Users.FindOneAndUpdateAsync(u => u.Id == userId,
                Builders<UserDoc>.Update.Combine(update));
            if (user is null)
            {
                return Results.NotFound();
            }

            if (req.Disabled == true)
            {
                await sessions.RevokeAllAsync(userId);
                active.CancelWhere(s => s.UserId == userId, CancelReasons.Revoked);
                await audit.WriteAsync(Actor.From(principal), "user.disabled", "user", id, user.Username);
            }
            else if (req.Disabled == false)
            {
                await audit.WriteAsync(Actor.From(principal), "user.enabled", "user", id, user.Username);
            }

            if (req.IsAdmin is { } a)
            {
                await audit.WriteAsync(Actor.From(principal), a ? "user.admin_granted" : "user.admin_revoked", "user",
                    id, user.Username);
            }

            return Results.NoContent();
        });

        g.MapDelete("/users/{id}", async (string id, ClaimsPrincipal principal, MongoStore store,
            ActiveStreams active, AuditService audit) =>
        {
            if (!Views.TryId(id, out var userId))
            {
                return Results.NotFound();
            }

            if (userId == principal.UserId())
            {
                return Bad("You cannot delete yourself.");
            }

            var user = await store.Users.FindOneAndDeleteAsync(u => u.Id == userId);
            if (user is null)
            {
                return Results.NotFound();
            }

            active.CancelWhere(s => s.UserId == userId, CancelReasons.Revoked);
            await store.ApiKeys.DeleteManyAsync(k => k.UserId == userId);
            await store.Grants.DeleteManyAsync(gr => gr.UserId == userId);
            await store.RefreshTokens.DeleteManyAsync(t => t.UserId == userId);
            await store.Invites.DeleteManyAsync(i => i.UserId == userId);
            await audit.WriteAsync(Actor.From(principal), "user.deleted", "user", id, user.Username);
            return Results.NoContent();
        });

        g.MapPost("/users/{id}/reset", async (string id, ClaimsPrincipal principal, MongoStore store,
            AuditService audit) =>
        {
            if (!Views.TryId(id, out var userId))
            {
                return Results.NotFound();
            }

            var user = await store.Users.Find(u => u.Id == userId).FirstOrDefaultAsync();
            if (user is null)
            {
                return Results.NotFound();
            }

            var (token, doc) = NewLink(InviteKind.PasswordReset, principal, userId, false);
            await store.Invites.InsertOneAsync(doc);
            await audit.WriteAsync(Actor.From(principal), "user.password_reset_issued", "user", id, user.Username);
            return Results.Ok(new { link = $"{options.PublicBaseUrl}/reset#{token}", expiresAt = doc.ExpiresAt });
        });

        g.MapGet("/invites", async (MongoStore store) =>
        {
            var invites = await store.Invites.Find(i => i.Kind == InviteKind.Invite).SortByDescending(i => i.CreatedAt)
                .Limit(100).ToListAsync();
            var users = (await store.Users.Find(FilterDefinition<UserDoc>.Empty).ToListAsync())
                .ToDictionary(u => u.Id, u => u.Username);
            return Results.Ok(invites.Select(i => new
            {
                id = i.Id.ToString(),
                i.CreatedAt,
                i.ExpiresAt,
                i.UsedAt,
                usedBy = i.UsedBy is { } ub ? users.GetValueOrDefault(ub, "deleted user") : null,
                admin = i.GrantAdmin,
                status = i.UsedAt is not null ? "used" : i.ExpiresAt < DateTime.UtcNow ? "expired" : "open",
            }));
        });

        g.MapPost("/invites", async (CreateInviteRequest req, ClaimsPrincipal principal, MongoStore store,
            AuditService audit) =>
        {
            var (token, doc) = NewLink(InviteKind.Invite, principal, null, req.Admin == true);
            await store.Invites.InsertOneAsync(doc);
            await audit.WriteAsync(Actor.From(principal), "invite.created", "invite", doc.Id.ToString());
            return Results.Ok(new { link = $"{options.PublicBaseUrl}/invite#{token}", expiresAt = doc.ExpiresAt });
        });

        g.MapDelete("/invites/{id}", async (string id, ClaimsPrincipal principal, MongoStore store,
            AuditService audit) =>
        {
            if (!Views.TryId(id, out var inviteId))
            {
                return Results.NotFound();
            }

            var result = await store.Invites.DeleteOneAsync(i => i.Id == inviteId && i.UsedAt == null);
            if (result.DeletedCount == 0)
            {
                return Results.NotFound();
            }

            await audit.WriteAsync(Actor.From(principal), "invite.revoked", "invite", id);
            return Results.NoContent();
        });

        g.MapGet("/audit", async (int? limit, MongoStore store) =>
        {
            var rows = await store.Audit.Find(FilterDefinition<AuditDoc>.Empty).SortByDescending(a => a.At)
                .Limit(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync();
            return Results.Ok(rows.Select(a => new
            {
                a.At,
                actor = a.ActorName,
                a.Action,
                a.TargetType,
                a.TargetName,
            }));
        });
    }

    private static (string Token, InviteDoc Doc) NewLink(InviteKind kind, ClaimsPrincipal principal, ObjectId? userId,
        bool admin)
    {
        var token = SecretTokens.NewOpaque();
        var now = DateTime.UtcNow;
        return (token, new InviteDoc
        {
            Id = ObjectId.GenerateNewId(),
            Kind = kind,
            TokenHash = SecretTokens.Hash(token),
            UserId = userId,
            GrantAdmin = admin,
            CreatedBy = principal.UserId(),
            CreatedAt = now,
            ExpiresAt = now.Add(AuthEndpoints.LinkLifetime),
        });
    }

    // ---------------------------------------------------------------- grants

    private static void MapGrants(RouteGroupBuilder g)
    {
        g.MapGet("/grants", async (MongoStore store) =>
        {
            var grants = await store.Grants.Find(FilterDefinition<GrantDoc>.Empty).ToListAsync();
            return Results.Ok(grants.Select(x => new
            {
                userId = x.UserId.ToString(),
                publicationId = x.PublicationId.ToString(),
            }));
        });

        // Grants either one publication, or every publication of an alias in one step.
        g.MapPut("/grants", async (SetGrantRequest req, ClaimsPrincipal principal, MongoStore store,
            ActiveStreams active, AuditService audit) =>
        {
            if (!Views.TryId(req.UserId, out var userId))
            {
                return Bad("Unknown user.");
            }

            var user = await store.Users.Find(u => u.Id == userId).FirstOrDefaultAsync();
            if (user is null)
            {
                return Bad("Unknown user.");
            }

            List<PublicationDoc> pubs;
            if (Views.TryId(req.PublicationId, out var pubId))
            {
                pubs = await store.Publications.Find(p => p.Id == pubId).ToListAsync();
            }
            else if (!string.IsNullOrEmpty(req.Alias))
            {
                pubs = await store.Publications.Find(p => p.Alias == req.Alias).ToListAsync();
            }
            else
            {
                return Bad("Give an alias or a publication.");
            }

            if (pubs.Count == 0)
            {
                return Bad("Unknown publication.");
            }

            foreach (var pub in pubs)
            {
                if (req.Granted)
                {
                    await store.Grants.UpdateOneAsync(x => x.UserId == userId && x.PublicationId == pub.Id,
                        Builders<GrantDoc>.Update
                            .SetOnInsert(x => x.CreatedAt, DateTime.UtcNow),
                        new UpdateOptions { IsUpsert = true });
                }
                else
                {
                    await store.Grants.DeleteOneAsync(x => x.UserId == userId && x.PublicationId == pub.Id);
                }
            }

            var what = req.Alias ?? pubs[0].Alias;
            await audit.WriteAsync(Actor.From(principal), req.Granted ? "grant.added" : "grant.removed", "user",
                user.Id.ToString(), $"{user.Username}: {what}");
            return Results.NoContent();
        });
    }

    // ---------------------------------------------------------------- usage

    private static void MapUsage(RouteGroupBuilder g)
    {
        g.MapGet("/usage", async (int? days, string? userId, string? alias, string? nodeId, MongoStore store) =>
        {
            var since = DateTime.UtcNow.Date.AddDays(-Math.Clamp(days ?? 30, 1, 3650));
            var f = Builders<UsageDailyDoc>.Filter;
            var filter = f.Gte(d => d.Day, since);
            if (Views.TryId(userId, out var uid))
            {
                filter &= f.Eq(d => d.UserId, uid);
            }

            if (!string.IsNullOrEmpty(alias))
            {
                filter &= f.Eq(d => d.Alias, alias);
            }

            if (Views.TryId(nodeId, out var nid))
            {
                filter &= f.Eq(d => d.NodeId, nid);
            }

            var rows = await store.UsageDaily.Find(filter).SortBy(d => d.Day).ToListAsync();
            var existing = (await store.Users.Find(FilterDefinition<UserDoc>.Empty).Project(u => u.Id).ToListAsync())
                .ToHashSet();
            return Results.Ok(rows.Select(r => new
            {
                day = r.Day.ToString("yyyy-MM-dd"),
                userId = r.UserId.ToString(),
                user = existing.Contains(r.UserId) ? r.Username : $"deleted user ({r.Username})",
                r.Alias,
                nodeId = r.NodeId.ToString(),
                node = r.NodeName,
                r.Requests,
                r.Errors,
                r.PromptTokens,
                r.CompletionTokens,
                r.DurationMs,
            }));
        });

        g.MapGet("/usage/recent", async (int? limit, MongoStore store) =>
        {
            var rows = await store.UsageRecords.Find(FilterDefinition<UsageRecordDoc>.Empty)
                .SortByDescending(r => r.StartedAt).Limit(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync();
            var existing = (await store.Users.Find(FilterDefinition<UserDoc>.Empty).Project(u => u.Id).ToListAsync())
                .ToHashSet();
            return Results.Ok(rows.Select(r => new
            {
                r.StartedAt,
                user = existing.Contains(r.UserId) ? r.Username : $"deleted user ({r.Username})",
                r.Alias,
                node = r.NodeName,
                r.Kind,
                r.Endpoint,
                r.QueueWaitMs,
                r.TimeToFirstByteMs,
                r.DurationMs,
                r.PromptTokens,
                r.CompletionTokens,
                r.Outcome,
                r.Status,
            }));
        });
    }
}

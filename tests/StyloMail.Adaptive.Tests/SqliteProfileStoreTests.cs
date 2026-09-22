using Microsoft.Data.Sqlite;
using StyloMail.Adaptive.Learning;
using StyloMail.Adaptive.Profiles;
using StyloMail.Adaptive.Storage;
using StyloMail.Core;
using StyloMail.Persistence;
using Xunit.Abstractions;

namespace StyloMail.Adaptive.Tests;

public sealed class SqliteProfileStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"stylomail-adaptive-{Guid.NewGuid():N}.db");

    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteAdaptiveProfileStore _store;
    private readonly ITestOutputHelper _output;

    public SqliteProfileStoreTests(ITestOutputHelper output)
    {
        _output = output;
        _factory = new SqliteConnectionFactory(_databasePath);
        _store = new SqliteAdaptiveProfileStore(_factory);
        _store.EnsureCreated();
    }

    [Fact]
    public void AProfileSurvivesARoundTrip()
    {
        var profile = Profile("tenant-a", "sender-a");
        for (var i = 0; i < 4; i++)
        {
            profile.Promote(Sample(0.5 + (i * 0.01)));
        }

        for (var i = 0; i < 3; i++)
        {
            profile.Observe(new ProfileObservation
            {
                ObservedAt = Start.AddMinutes(i),
                RecipientCount = 2,
                WasRejected = i == 2,
                Dimensions = DimensionVector.Create(DimensionSample.Available("d1", 0.5)),
            });
        }

        _store.Save(profile, Start);

        var loaded = _store.Load(profile.Key);

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded.Observed.Attempts);
        Assert.Equal(6, loaded.Observed.Recipients);
        Assert.Equal(1, loaded.Observed.RejectedAttempts);
        Assert.Equal(4, loaded.Baseline.TrustedSupport);
        Assert.Equal(profile.Baseline.Dimensions["d1"].Mean, loaded.Baseline.Dimensions["d1"].Mean, 9);
        Assert.NotNull(loaded.Baseline.ScaleModel);
        Assert.True(loaded.FastAverages["d1"].HasValue);
        Assert.True(loaded.SlowAverages["d1"].HasValue);
    }

    [Fact]
    public void TenantIsolationIsAbsolute()
    {
        var tenantA = Profile("tenant-a", "shared-key");
        var tenantB = Profile("tenant-b", "shared-key");

        // Deliberately identical profile keys in two tenants: the pseudonym is tenant-scoped,
        // but the store must also never let one tenant address the other's row.
        tenantA.Observe(new ProfileObservation { ObservedAt = Start, RecipientCount = 3, WasRejected = false });
        tenantB.Observe(new ProfileObservation { ObservedAt = Start, RecipientCount = 99, WasRejected = false });

        _store.Save(tenantA, Start);
        _store.Save(tenantB, Start);

        var loadedA = _store.Load(tenantA.Key);
        var loadedB = _store.Load(tenantB.Key);

        Assert.Equal(3, loadedA!.Observed.Recipients);
        Assert.Equal(99, loadedB!.Observed.Recipients);
        Assert.Equal("tenant-a", loadedA.Key.TenantId);
        Assert.Equal("tenant-b", loadedB.Key.TenantId);
    }

    [Fact]
    public void AProfileThatDoesNotExistForATenantIsAbsentRatherThanBorrowed()
    {
        _store.Save(Profile("tenant-a", "sender-a"), Start);

        var borrowed = _store.Load(Profile("tenant-b", "sender-a").Key);

        Assert.Null(borrowed);
    }

    [Fact]
    public void ATenantListingContainsOnlyItsOwnProfiles()
    {
        _store.Save(Profile("tenant-a", "sender-a"), Start);
        _store.Save(Profile("tenant-a", "sender-b", ProfileScopeKind.Recipient), Start);
        _store.Save(Profile("tenant-b", "sender-c"), Start);

        var forA = _store.LoadForTenant("tenant-a");

        Assert.Equal(2, forA.Count);
        Assert.All(forA, profile => Assert.Equal("tenant-a", profile.Key.TenantId));
    }

    [Fact]
    public void FreezeAndRegimeStateSurviveARestart()
    {
        var profile = Profile("tenant-a", "sender-a");
        profile.Promote(Sample(0.5));
        profile.FreezeBaseline("suspected compromise", Start);

        _store.Save(profile, Start);

        var loaded = _store.Load(profile.Key);

        Assert.True(loaded!.Baseline.IsFrozen);
        Assert.Equal("suspected compromise", loaded.Baseline.FreezeReason);
        Assert.Equal(profile.CurrentRegimeId, loaded.CurrentRegimeId);
    }

    [Fact]
    public void SavingIsIdempotentRatherThanCountingTwice()
    {
        var profile = Profile("tenant-a", "sender-a");
        profile.Observe(new ProfileObservation { ObservedAt = Start, RecipientCount = 4, WasRejected = false });

        _store.Save(profile, Start);
        _store.Save(profile, Start.AddMinutes(1));

        var loaded = _store.Load(profile.Key);

        Assert.Equal(1, loaded!.Observed.Attempts);
        Assert.Equal(4, loaded.Observed.Recipients);
    }

    [Fact]
    public void EvictionDehydratesTheBaselineAndKeepsTheCounters()
    {
        var profile = Profile("tenant-a", "sender-a");
        profile.Promote(Sample(0.5));
        for (var i = 0; i < 5; i++)
        {
            profile.Observe(new ProfileObservation { ObservedAt = Start.AddMinutes(i), RecipientCount = 4, WasRejected = false });
        }

        _store.Save(profile, Start);

        var quota = new SendingQuotaLedger(recipientsPerWindow: 100, TimeSpan.FromHours(1));
        Assert.True(quota.TryReserve("tenant-a", "sender-a", 20, Start));

        _store.Evict(profile.Key, Start);

        var afterEviction = _store.Load(profile.Key);

        // Evicting the profile frees the expensive baseline state and keeps the counters. A
        // profile that came back with its observed history erased would have earned a fresh
        // quota, and quotas are the only thing bounding a late detection.
        Assert.NotNull(afterEviction);
        Assert.Equal(0, afterEviction.Baseline.TrustedSupport);
        Assert.Null(afterEviction.Baseline.ScaleModel);
        Assert.Equal(5, afterEviction.Observed.Attempts);
        Assert.Equal(20, afterEviction.Observed.Recipients);
        Assert.Equal(80, quota.Remaining("tenant-a", "sender-a", Start));
    }

    [Fact]
    public void ABucketSeriesSurvivesARestart()
    {
        var profile = Profile("tenant-a", "sender-a");
        for (var i = 0; i < 3; i++)
        {
            profile.Observe(new ProfileObservation
            {
                ObservedAt = Start.AddMinutes(i).AddSeconds(1),
                RecipientCount = 5,
                WasRejected = false,
                Dimensions = DimensionVector.Create(DimensionSample.Available("d1", 0.4 + (i * 0.1))),
            });
        }

        _store.Save(profile, Start);

        var loaded = _store.Load(profile.Key);
        var buckets = loaded!.Series["burst"].Buckets;

        Assert.Equal(3, buckets.Count);
        Assert.Equal(5, buckets[0].RecipientCount);
        Assert.Equal(0.4, buckets[0].MeanDimensions(1).ValueOf("d1")!.Value, 9);
        Assert.Equal(0.6, buckets[2].MeanDimensions(1).ValueOf("d1")!.Value, 9);
    }

    [Fact]
    public void TheCentroidIndexNeverAnswersAcrossTenants()
    {
        var centroids = new SemanticCentroidStore(_factory);
        var embedding = Enumerable.Repeat(0.5f, 12).ToArray();

        // Two tenants, one identical embedding. A query for one must never return the other's
        // nearest neighbour, and must return the other's row to nobody.
        centroids.Upsert("tenant-a", "sender", "key-a", embedding, "adaptive-dimensions/1", Start);
        centroids.Upsert("tenant-b", "sender", "key-b", embedding, "adaptive-dimensions/1", Start);

        var matches = centroids.FindNearest("tenant-a", "sender", embedding, k: 5, "adaptive-dimensions/1");

        Assert.Single(matches);
        Assert.Equal("tenant-a", matches[0].TenantId);
        Assert.Equal("key-a", matches[0].ProfileKey);
    }

    [Fact]
    public void AStaleWriteIsRejectedRatherThanSilentlyOverwriting()
    {
        var profile = Profile("tenant-a", "sender-a");
        profile.Observe(new ProfileObservation
        {
            ObservedAt = Start,
            RecipientCount = 4,
            WasRejected = false,
        });
        _store.Save(profile, Start);

        // Two threads that both loaded the same revision, both mutated, both saving.
        var first = _store.Load(profile.Key)!;
        var second = _store.Load(profile.Key)!;

        first.Observe(new ProfileObservation
        {
            ObservedAt = Start.AddMinutes(1),
            RecipientCount = 4,
            WasRejected = false,
        });
        second.Observe(new ProfileObservation
        {
            ObservedAt = Start.AddMinutes(2),
            RecipientCount = 4,
            WasRejected = false,
        });

        _store.Save(first, Start.AddMinutes(1));

        // The second write is a load-modify-save over state that has already moved. Silently
        // accepting it would drop `first`'s observation, and for observed counters a silent
        // loss means the abuse-bounding counts read low.
        var conflict = Assert.Throws<ProfileVersionConflictException>(
            () => _store.Save(second, Start.AddMinutes(2)));

        Assert.Equal("tenant-a", conflict.Key.TenantId);
        Assert.Equal(conflict.ExpectedRevision, conflict.ActualRevision - 1);
    }

    [Fact]
    public void ARejectedWriteLeavesTheStoredProfileExactlyAsTheWinnerLeftIt()
    {
        var profile = Profile("tenant-a", "sender-a");
        profile.Promote(Sample(0.5));
        _store.Save(profile, Start);

        var winner = _store.Load(profile.Key)!;
        var loser = _store.Load(profile.Key)!;

        winner.Observe(new ProfileObservation
        {
            ObservedAt = Start.AddMinutes(1),
            RecipientCount = 7,
            WasRejected = false,
        });
        loser.Promote(Sample(0.9));
        loser.Observe(new ProfileObservation
        {
            ObservedAt = Start.AddMinutes(2),
            RecipientCount = 99,
            WasRejected = false,
        });

        _store.Save(winner, Start.AddMinutes(1));
        Assert.Throws<ProfileVersionConflictException>(() => _store.Save(loser, Start.AddMinutes(2)));

        var stored = _store.Load(profile.Key)!;

        // Neither half of the loser's write landed: not the counters, and not the baseline.
        // A half-applied write would leave the profile describing a state that never existed.
        Assert.Equal(1, stored.Observed.Attempts);
        Assert.Equal(7, stored.Observed.Recipients);
        Assert.Equal(1, stored.Baseline.TrustedSupport);
    }

    [Fact]
    public void AVersionConflictIsDistinguishableFromAStorageFailure()
    {
        var profile = Profile("tenant-a", "sender-a");
        _store.Save(profile, Start);

        var stale = _store.Load(profile.Key)!;
        profile.Observe(new ProfileObservation
        {
            ObservedAt = Start,
            RecipientCount = 1,
            WasRejected = false,
        });
        _store.Save(profile, Start);

        var conflict = Assert.Throws<ProfileVersionConflictException>(
            () => _store.Save(stale, Start));

        // A lost update is a correctness event the caller resolves by reloading, not a transient
        // blip to retry blindly. It must not be catchable as storage trouble, so that a handler
        // written for SQLITE_BUSY does not swallow it. Checked by reflection over the runtime
        // type rather than a pattern match, which would only restate the static type.
        Assert.False(
            typeof(SqliteException).IsAssignableFrom(conflict.GetType()),
            "a version conflict must not be catchable as a storage failure");

        Assert.True(conflict.ActualRevision > conflict.ExpectedRevision);
    }

    [Fact]
    public void ReloadingAfterAConflictAllowsTheWriteToSucceed()
    {
        var profile = Profile("tenant-a", "sender-a");
        _store.Save(profile, Start);

        var stale = _store.Load(profile.Key)!;
        profile.Observe(new ProfileObservation
        {
            ObservedAt = Start,
            RecipientCount = 2,
            WasRejected = false,
        });
        _store.Save(profile, Start);

        Assert.Throws<ProfileVersionConflictException>(() => _store.Save(stale, Start));

        // The documented remedy: reload, reapply, save.
        var reloaded = _store.Load(profile.Key)!;
        reloaded.Observe(new ProfileObservation
        {
            ObservedAt = Start.AddMinutes(5),
            RecipientCount = 3,
            WasRejected = false,
        });
        _store.Save(reloaded, Start.AddMinutes(5));

        Assert.Equal(2, _store.Load(profile.Key)!.Observed.Attempts);
    }

    [Fact]
    public void TheRevisionAdvancesOnEverySaveNotOnlyOnPromotion()
    {
        var profile = Profile("tenant-a", "sender-a");
        _store.Save(profile, Start);

        var reloaded = _store.Load(profile.Key)!;
        var revisionBefore = reloaded.PersistedRevision;
        var baselineVersionBefore = reloaded.Baseline.Version;

        reloaded.Observe(new ProfileObservation
        {
            ObservedAt = Start.AddMinutes(1),
            RecipientCount = 1,
            WasRejected = false,
        });
        _store.Save(reloaded, Start.AddMinutes(1));

        var stored = _store.Load(profile.Key)!;

        // An observation moves no baseline version. If the token were the baseline version,
        // it would be unchanged here, and this write would silently overwrite anything that
        // landed in between. The revision has to move on every write to be a usable token.
        Assert.Equal(baselineVersionBefore, stored.Baseline.Version);
        Assert.True(stored.PersistedRevision > revisionBefore);
    }

    [Fact]
    public async Task ConcurrentCreatorsOfABrandNewProfileCannotBothReportSuccess()
    {
        const int writers = 16;

        // Every writer sees the same thing: no profile row, so nothing to load, and a
        // freshly constructed profile sitting at revision 0. All of them then try to create it.
        using var start = new Barrier(writers);
        var successes = 0;
        var conflicts = 0;
        var storageErrors = 0;
        var unexpected = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            var profile = Profile("tenant-a", "brand-new");
            profile.Observe(new ProfileObservation
            {
                ObservedAt = Start.AddSeconds(writer),
                RecipientCount = 1,
                WasRejected = false,
            });

            start.SignalAndWait();
            try
            {
                _store.Save(profile, Start.AddSeconds(writer));
                Interlocked.Increment(ref successes);
            }
            catch (ProfileVersionConflictException)
            {
                Interlocked.Increment(ref conflicts);
            }
            catch (SqliteException)
            {
                // A storage-level refusal here would mean the loser cannot tell a lost update
                // from a busy lock, which is exactly what the distinct exception exists to prevent.
                Interlocked.Increment(ref storageErrors);
            }
            catch (Exception ex)
            {
                unexpected.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Empty(unexpected);

        // Surfaced rather than inferred: for a concurrency test the distribution is the evidence,
        // and "it was green" is not the same claim as "exactly one writer won".
        _output.WriteLine($"successes={successes} conflicts={conflicts} storage_errors={storageErrors}");

        var stored = _store.Load(Profile("tenant-a", "brand-new").Key);

        // Two writers both reading revision 0 and both passing the swap would both report
        // success while only one row survives, a silently dropped observation, which is the
        // failure this whole mechanism exists to make impossible. Counting reported successes
        // against what actually landed is what catches it.
        Assert.Equal(0, storageErrors);
        Assert.Equal(writers, successes + conflicts);
        Assert.True(stored is not null);
        Assert.Equal(successes, stored.Observed.Attempts);
    }

    [Fact]
    public async Task ConcurrentDeltasOnOneProfileAllLandWithoutConflicts()
    {
        const int writers = 16;
        var key = Profile("tenant-a", "burst-sender").Key;
        _store.Save(Profile("tenant-a", "burst-sender"), Start);

        using var start = new Barrier(writers);
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                _store.ApplyObservation(
                    key,
                    new ProfileObservation
                    {
                        ObservedAt = Start.AddSeconds(writer),
                        RecipientCount = 1,
                        WasRejected = false,
                    },
                    Start.AddSeconds(writer));
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Sixteen messages from one sender at once is not a hot edge case, it is the shape of
        // the very burst this system exists to notice, and the profile written by many messages
        // at once is the compromised account. A delta path that made the caller retry would
        // strand most of them; every observation must land, and none may be lost.
        Assert.Empty(failures);
        Assert.Equal(writers, _store.Load(key)!.Observed.Attempts);
        Assert.Equal(writers, _store.Load(key)!.Observed.Recipients);
    }

    [Fact]
    public async Task ConcurrentDeltasCanCreateAProfileThatDoesNotExistYet()
    {
        const int writers = 8;
        var key = Profile("tenant-a", "never-seen").Key;

        using var start = new Barrier(writers);
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                _store.ApplyObservation(
                    key,
                    new ProfileObservation
                    {
                        ObservedAt = Start.AddSeconds(writer),
                        RecipientCount = 2,
                        WasRejected = false,
                    },
                    Start.AddSeconds(writer));
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Empty(failures);
        Assert.Equal(writers, _store.Load(key)!.Observed.Attempts);
    }

    [Fact]
    public void ADeltaDoesNotCrossTenants()
    {
        _store.Save(Profile("tenant-a", "shared-key"), Start);
        _store.Save(Profile("tenant-b", "shared-key"), Start);

        _store.ApplyObservation(
            Profile("tenant-a", "shared-key").Key,
            new ProfileObservation { ObservedAt = Start, RecipientCount = 5, WasRejected = false },
            Start);

        Assert.Equal(1, _store.Load(Profile("tenant-a", "shared-key").Key)!.Observed.Attempts);
        Assert.Equal(0, _store.Load(Profile("tenant-b", "shared-key").Key)!.Observed.Attempts);
    }

    [Fact]
    public void AnUpdateAppliesTheChangeAndReturnsTheDelegatesResult()
    {
        var key = Profile("tenant-a", "sender-a").Key;
        _store.Save(Profile("tenant-a", "sender-a"), Start);

        var outcome = _store.Update(key, Start.AddMinutes(1), profile =>
        {
            var result = profile.Promote(Sample(0.5));
            profile.Observe(new ProfileObservation
            {
                ObservedAt = Start.AddMinutes(1),
                RecipientCount = 3,
                WasRejected = false,
            });

            return result;
        });

        Assert.Equal(PromotionOutcome.Promoted, outcome);

        var stored = _store.Load(key)!;
        Assert.Equal(1, stored.Baseline.TrustedSupport);
        Assert.Equal(1, stored.Observed.Attempts);
    }

    [Fact]
    public void AnUpdateCanCreateAProfileThatDoesNotExistYet()
    {
        var key = Profile("tenant-a", "never-seen").Key;

        _store.Update(key, Start, profile =>
        {
            profile.Observe(new ProfileObservation
            {
                ObservedAt = Start,
                RecipientCount = 2,
                WasRejected = false,
            });

            return true;
        });

        Assert.Equal(1, _store.Load(key)!.Observed.Attempts);
    }

    [Fact]
    public async Task PromotionsRacingABurstNeitherConflictNorLoseObservations()
    {
        var key = Profile("tenant-a", "busy-sender").Key;
        _store.Save(Profile("tenant-a", "busy-sender"), Start);

        const int observations = 200;
        const int promotions = 40;
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        using var start = new Barrier(2);

        var burst = Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < observations; i++)
            {
                try
                {
                    _store.ApplyObservation(
                        key,
                        new ProfileObservation
                        {
                            ObservedAt = Start.AddMilliseconds(i),
                            RecipientCount = 1,
                            WasRejected = false,
                        },
                        Start.AddMilliseconds(i));
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        });

        var promoter = Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < promotions; i++)
            {
                try
                {
                    _store.Update(key, Start.AddSeconds(i), profile => profile.Promote(Sample(0.5)));
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        });

        await Task.WhenAll(burst, promoter);

        // A promotion racing a burst is not a corner case: it is an operator intervening in
        // exactly the incident that produces the burst, deciding the sender's new behaviour is
        // legitimate. The moment the operation most needs to succeed is the moment it is most
        // likely to fail under a compare-and-swap-and-retry design, and a lost observation here
        // is silent, because the counters simply read low.
        Assert.Empty(failures);

        var stored = _store.Load(key)!;
        Assert.Equal(observations, stored.Observed.Attempts);
        Assert.Equal(promotions, stored.Baseline.Version);
    }

    [Fact]
    public void AThrowingUpdatePropagatesAndWritesNothing()
    {
        var key = Profile("tenant-a", "sender-a").Key;
        _store.Save(Profile("tenant-a", "sender-a"), Start);

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            // Explicit type argument: a delegate whose body only throws has no inferable result.
            _store.Update<object?>(key, Start, profile =>
            {
                profile.Observe(new ProfileObservation
                {
                    ObservedAt = Start,
                    RecipientCount = 5,
                    WasRejected = false,
                });

                throw new InvalidOperationException("the decision could not be made");
            }));

        Assert.Equal("the decision could not be made", thrown.Message);

        // The callback held a mutable profile and had already changed it. A half-applied write
        // would leave the store describing a state that was never decided.
        var stored = _store.Load(key)!;
        Assert.Equal(0, stored.Observed.Attempts);
        Assert.Equal(0, stored.Baseline.Version);
    }

    [Fact]
    public void AnUpdateDoesNotCrossTenants()
    {
        _store.Save(Profile("tenant-a", "shared-key"), Start);
        _store.Save(Profile("tenant-b", "shared-key"), Start);

        _store.Update(Profile("tenant-a", "shared-key").Key, Start, profile =>
        {
            profile.Observe(new ProfileObservation
            {
                ObservedAt = Start,
                RecipientCount = 7,
                WasRejected = false,
            });

            return true;
        });

        Assert.Equal(1, _store.Load(Profile("tenant-a", "shared-key").Key)!.Observed.Attempts);
        Assert.Equal(0, _store.Load(Profile("tenant-b", "shared-key").Key)!.Observed.Attempts);
    }

    [Fact]
    public void TheRecipientHistorySurvivesARoundTrip()
    {
        var key = Profile("tenant-a", "sender-a").Key;
        _store.Update(key, Start, profile =>
        {
            profile.Observe(new ProfileObservation
            {
                ObservedAt = Start,
                RecipientCount = 2,
                WasRejected = false,
                RecipientKeys = ["alice", "bob"],
                Dimensions = DimensionVector.Create(
                    DimensionSample.Available("semantic.credential_request", 0.05)),
            });

            return true;
        });

        var reloaded = _store.Load(key)!;

        // Without this the filter starts empty on every load, and an empty filter reports every
        // recipient as never seen — manufacturing alarm on the strongest signal the profile
        // carries, on every single load.
        Assert.True(reloaded.Recipients.IsComplete);
        Assert.Equal(2, reloaded.Recipients.Count);
        Assert.Equal(0, reloaded.Recipients.NovelCount(["alice"]));
        Assert.Equal(2, reloaded.Recipients.NovelCount(["stranger-one", "stranger-two"]));
    }

    [Fact]
    public void AProfileWithHistoryButNoStoredFilterReportsNoveltyAsUnknown()
    {
        var profile = Profile("tenant-a", "sender-a");
        profile.Observe(new ProfileObservation
        {
            ObservedAt = Start,
            RecipientCount = 1,
            WasRejected = false,
            RecipientKeys = ["alice"],
        });

        _store.Save(profile, Start);

        // Rewrite the state document without the membership filter, which is exactly what a row
        // written before recipient tracking existed looks like: real observed traffic, no filter.
        using (var connection = _factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE profiles SET bucket_state_json = '{}' WHERE tenant_id = $t AND profile_key LIKE 'sender-a%';";
            command.Parameters.AddWithValue("$t", "tenant-a");
            command.ExecuteNonQuery();
        }

        var reloaded = _store.Load(profile.Key)!;

        // Whatever the cause, a filter that was not restored cannot answer "have we ever seen
        // them", and answering "no" for everyone is the alarm we must not manufacture.
        Assert.False(reloaded.Recipients.IsComplete);
        Assert.Null(reloaded.Recipients.NovelCount(["stranger"]));
    }

    [Fact]
    public void EnsureCreatedIsSafeToRunRepeatedly()
    {
        _store.EnsureCreated();
        _store.EnsureCreated();

        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM profiles;";
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        GC.SuppressFinalize(this);
    }

    private static DimensionVector DimensionVectorFor(double value) =>
        DimensionVector.Create(DimensionSample.Available("d1", value));

    private static AdaptiveProfile Profile(
        string tenantId,
        string key,
        ProfileScopeKind scope = ProfileScopeKind.OutboundSender) =>
        new(new ProfileKey
        {
            TenantId = tenantId,
            Scope = scope,
            Key = key,
            Direction = MailDirection.Outbound,
        });

    private static TrustedSample Sample(double value) => new()
    {
        RecordedAt = Start,
        Provenance = LabelProvenance.AuthenticatedOperator,
        Dimensions = DimensionVectorFor(value),
    };
}

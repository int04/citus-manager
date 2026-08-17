using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class RebalanceStatusParserTests
{
    [Fact]
    public void Parser_correlates_requested_job_and_reads_typed_progress()
    {
        const string json = """
            [{"job_id":40,"state":"finished"},
             {"job_id":41,"state":"running","moves_processed":2,"moves_total":5,
              "bytes_processed":100,"bytes_total":400,"source_host":"w1","target_host":"w2","shard_id":7}]
            """;

        var status = CitusMutator.ParseRebalanceStatus(json, 41);

        Assert.Equal(41, status.JobId);
        Assert.Equal("running", status.State);
        Assert.Equal(2, status.MovesProcessed);
        Assert.Equal(5, status.MovesTotal);
        Assert.Equal(100, status.BytesProcessed);
        Assert.Equal("w1", status.CurrentSource);
        Assert.Equal(7, status.CurrentShard);
        Assert.False(status.IsComplete);
    }

    [Fact]
    public void Empty_status_is_terminal_without_fake_byte_progress()
    {
        var status = CitusMutator.ParseRebalanceStatus("[]", 12);

        Assert.True(status.IsComplete);
        Assert.Null(status.BytesTotal);
        Assert.Equal(12, status.JobId);
    }

    [Fact]
    public void Parser_aggregates_official_details_colocation_shape()
    {
        const string json = """
            [{"job_id":9,"state":"running","details":{"phase":"copy","colocations":{
              "1":{"shard_moves":30,"shard_moved":29},"2":{"shard_moves":10,"shard_moved":3}}}}]
            """;

        var status = CitusMutator.ParseRebalanceStatus(json, 9);

        Assert.Equal(32, status.MovesProcessed);
        Assert.Equal(40, status.MovesTotal);
        Assert.Equal("copy", status.CurrentTable);
    }

    [Fact]
    public void Parser_reads_task_state_counts_returned_by_citus_14()
    {
        const string json = """
            [{"job_id":2,"state":"finished","details":{"tasks":[],"task_state_counts":{"done":78}}}]
            """;

        var status = CitusMutator.ParseRebalanceStatus(json, 2);

        Assert.Equal(78, status.MovesProcessed);
        Assert.Equal(78, status.MovesTotal);
        Assert.True(status.IsComplete);
    }

    [Fact]
    public void Legacy_parser_reports_per_shard_progress_and_bytes()
    {
        const string json = """
            [{"shardid":1,"shard_size":100,"progress":2,"target_shard_size":100},
             {"shardid":2,"shard_size":300,"progress":1,"target_shard_size":120,"sourcename":"w1","targetname":"w2"}]
            """;

        var status = CitusMutator.ParseLegacyRebalanceProgress(json, null);

        Assert.Equal(1, status.MovesProcessed);
        Assert.Equal(2, status.MovesTotal);
        Assert.Equal(220, status.BytesProcessed);
        Assert.Equal(400, status.BytesTotal);
        Assert.Equal("w1", status.CurrentSource);
    }
}

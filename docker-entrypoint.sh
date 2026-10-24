#!/usr/bin/env bash
# Run Dumplings against the local Core and publish its Scanner/ to S3.
#
# Every pass: wait until Core (and its txindex, if any) is synced, publish the canonical
# hashes of the last HASH_WINDOW heights (<destination>/block-hashes.json, the
# trusted checkpoints of the daily BlockSci index update), run `dumplings sync`
# (it resumes from Scanner/LastProcessedBlockHeight.txt), and when the height
# moved, upload Scanner/ to <destination>/<height>/ and point
# <destination>/latest.json at it. The latest snapshot is what readers use; the
# one before it stays one more pass for a reader still downloading it, older
# ones are deleted (scenario-lab/mainnet-analysis-plan-v2.md §2.7).
#
# Dumplings reaches Core on localhost:8332, so the container shares Core's
# network namespace. A failed sync publishes nothing and is retried next pass.
set -euo pipefail

cookie=${BITCOIN_RPC_COOKIE:-/bitcoin/.cookie}
destination=${DUMPLINGS_S3_DESTINATION:?set DUMPLINGS_S3_DESTINATION, e.g. s3://bucket/dumplings}
interval=${DUMPLINGS_INTERVAL:-86400}
retry=${DUMPLINGS_RETRY:-600}
hash_window=${HASH_WINDOW:-2016}
scanner=/data/Scanner
commit=$(cat /app/COMMIT)

credentials_file=${S3_CREDENTIALS_FILE:-}
if [ -n "$credentials_file" ]; then
    if [ ! -f "$credentials_file" ] || [ ! -r "$credentials_file" ]; then
        echo "S3_CREDENTIALS_FILE must name a readable regular file" >&2
        exit 2
    fi
elif [ -n "${S3_ACCESS_KEY_ID:-}" ]; then
    # Without a mounted file, s5cmd reads AWS_* credentials from the environment.
    export AWS_ACCESS_KEY_ID=$S3_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY=${S3_SECRET_ACCESS_KEY:?}
fi

s5() {
    local options=()
    if [ -n "$credentials_file" ]; then
        options+=(--credentials-file "$credentials_file" --profile "${S3_PROFILE:-coinjoin}")
    elif [ -n "${S3_PROFILE:-}" ] && [ -z "${AWS_ACCESS_KEY_ID:-}" ] && [ -z "${AWS_SECRET_ACCESS_KEY:-}" ]; then
        options+=(--profile "$S3_PROFILE")
    fi
    if [ -n "${S3_ENDPOINT_URL:-}" ]; then
        options+=(--endpoint-url "$S3_ENDPOINT_URL")
    fi
    s5cmd "${options[@]}" "$@"
}

rpc() {
    curl -fsS --user "$(cat "$cookie")" -H 'content-type: application/json' \
        --data "{\"jsonrpc\":\"1.0\",\"id\":\"dumplings\",\"method\":\"$1\",\"params\":${2:-[]}}" \
        http://127.0.0.1:8332/ | jq -c .result
}

wait_for_core() {
    until [ -r "$cookie" ] && rpc getblockchaininfo >/dev/null 2>&1; do sleep 30; done
    # Dumplings keeps the Whirlpool tx0s it scanned in Scanner/Tx0Candidates.txt and
    # asks a txindex only for older ones, so a txindex is optional but must be complete.
    until [ "$(rpc getindexinfo '["txindex"]' | jq -r 'if has("txindex") then .txindex.synced else true end')" = true ]; do
        echo "waiting for Core's txindex to finish"
        sleep 300
    done
    until [ "$(rpc getblockchaininfo | jq -r '.headers == .blocks')" = true ]; do
        echo "waiting for Core to catch up with its headers"
        sleep 300
    done
}

# One batched JSON-RPC call: {"tip": H, "hashes": {"<height>": "<hash>", ...}}.
publish_hashes() {
    local tip from
    tip=$(rpc getblockcount)
    from=$(( tip > hash_window ? tip - hash_window : 0 ))
    jq -nc --argjson from "$from" --argjson to "$tip" \
        '[range($from; $to + 1) | {jsonrpc: "1.0", id: ., method: "getblockhash", params: [.]}]' \
        | curl -fsS --user "$(cat "$cookie")" -H 'content-type: application/json' --data @- http://127.0.0.1:8332/ \
        | jq -c --argjson tip "$tip" '{tip: $tip, hashes: (map({key: (.id | tostring), value: .result}) | from_entries)}' \
        > /tmp/block-hashes.json
    s5 cp /tmp/block-hashes.json "$destination/block-hashes.json" >/dev/null
    echo "published hashes of heights $from-$tip"
}

publish() {
    local height=$1 latest current previous
    latest=$(s5 cat "$destination/latest.json" 2>/dev/null || true)
    current=$(jq -r '.prefix // empty' <<<"$latest" 2>/dev/null || true)
    previous=$(jq -r '.previous // empty' <<<"$latest" 2>/dev/null || true)
    if [ "$current" = "$destination/$height" ]; then
        return
    fi
    # Tx0Candidates.txt only serves the scan itself and would bloat every download.
    s5 cp --exclude 'Tx0Candidates.txt' "$scanner/*" "$destination/$height/" >/dev/null
    jq -n --arg prefix "$destination/$height" --arg previous "$current" --argjson height "$height" \
        --arg commit "$commit" --arg uploaded "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        '{last_processed_height: $height, prefix: $prefix, previous: $previous, dumplings_commit: $commit, uploaded_at: $uploaded}' \
        > /tmp/latest.json
    s5 cp /tmp/latest.json "$destination/latest.json" >/dev/null
    # The snapshot before the switch may still be downloading; the one before that goes.
    if [ -n "$previous" ] && [ "$previous" != "$destination/$height" ]; then
        s5 rm "$previous/*" >/dev/null || echo "could not delete $previous; it stays until the next pass"
    fi
    echo "published $destination/$height"
}

while true; do
    wait_for_core
    publish_hashes || echo "could not publish block hashes" >&2
    started=$(date +%s)
    if dotnet /app/Dumplings.Cli.dll sync --rpccookiefile="$cookie" </dev/null; then
        height=$(cat "$scanner/LastProcessedBlockHeight.txt")
        echo "sync reached height $height in $(( $(date +%s) - started )) s"
        # Published with every snapshot; enable pruning only while it stays empty.
        echo "tx0s missed by the candidate rule: $(wc -l < "$scanner/Tx0CandidateMisses.txt" 2>/dev/null || echo 0)"
        publish "$height"
        pause=$interval
    else
        # Often transient (a new header before its block); try again soon, not a day later.
        echo "sync failed; nothing published, retrying in ${retry}s" >&2
        pause=$retry
    fi
    [ "$interval" = 0 ] && break
    sleep "$pause"
done

#!/bin/sh
# Each invocation runs in this job's private directory.
umask 077
LC_ALL=en_US.UTF-8
export LC_ALL

reply() {
    printf '{"phase":"%s","ok":true}\n' "$1"
}

error_json() {
    printf '{"ok":false,"error":"%s"}\n' "$1"
}

cli_path() {
    cli=$(/bin/cat cli-path.txt) || return 1
    case "$cli" in /*) [ -x "$cli" ] ;; *) return 1 ;; esac
}

app_session() {
    app_pid=$(/usr/bin/pgrep -u "$(/usr/bin/id -u)" -x Siliconed) || return 1
    case "$app_pid" in ''|*[!0-9]*) return 1 ;; esac
    app_started=$(/bin/ps -p "$app_pid" -o lstart=) || return 1
    [ -n "$app_started" ] && printf '%s %s\n' "$app_pid" "$app_started"
}

same_session() {
    live=$(app_session) && [ "$live" = "$(/bin/cat app-session 2>/dev/null)" ]
}

own_job() {
    job=$(/usr/bin/plutil -extract job raw -o - accepted.json 2>/dev/null) || return 1
    case "$job" in t[0-9]*) case "${job#t}" in *[!0-9]*) return 1 ;; esac ;; *) return 1 ;; esac
}

preflight() {
    cli_path || { error_json cli_missing; return; }
    session=$(app_session) || { error_json app_not_running; return; }
    /bin/mkdir output || { error_json write; return; }
    printf '%s\n' "$session" > app-session
    "$cli" status > rack.json 2>/dev/null
    local_compute=$(/usr/bin/plutil -type computing_on rack.json 2>/dev/null)
    if [ "$local_compute" != "(any)" ]; then
        if [ "$(/usr/bin/plutil -extract ok raw -o - rack.json 2>/dev/null)" != true ]; then
            /bin/cat rack.json
            return
        fi
        error_json remote_compute
        return
    fi
    same_session || { error_json app_changed; return; }
    p=$(/bin/cat prompt.txt && printf x) || { error_json unreadable_prompt; return; }
    "$cli" add --model qwen-image-2.1 --ref input.png "${p%x}" 2>/dev/null
}

publish_wait() {
    # A dropped session may leave image events without the final verdict.
    complete=$(/usr/bin/tail -n 1 "$1" | /usr/bin/plutil -extract ok raw -o - - 2>/dev/null)
    case "$complete" in
        true|false) /bin/mv "$1" wait.jsonl ;;
    esac
}

mode=$1
case "$mode" in
    launch)
        if /bin/mkdir submitted 2>/dev/null; then
            /usr/bin/nohup /bin/sh ./bridge.sh work </dev/null >>bridge.stderr 2>&1 &
            printf '%s\n' "$!" > worker.pid
        elif [ ! -d submitted ]; then
            error_json write
            exit 0
        fi
        reply submitted
        ;;
    work)
        preflight > accepted.part
        /bin/mv accepted.part accepted.json || exit 1
        own_job || exit 0
        if same_session; then
            "$cli" wait "$job" --out output > wait.part 2>/dev/null
        else
            error_json app_changed > wait.part
        fi
        publish_wait wait.part
        ;;
    probe)
        if [ ! -d submitted ]; then
            reply not_submitted
            exit 0
        fi
        if [ -s accepted.json ]; then
            reply accepted
            /bin/cat accepted.json
        fi
        if [ -f wait.jsonl ]; then
            reply finished
            /bin/cat wait.jsonl
        elif [ -s accepted.json ] && own_job; then
            reply status
            if ! same_session; then
                error_json app_changed
            elif cli_path; then
                "$cli" status 2>/dev/null
            else
                error_json cli_missing
            fi
        fi
        worker_pid=$(/bin/cat worker.pid 2>/dev/null)
        if [ -n "$worker_pid" ] && /bin/kill -0 "$worker_pid" 2>/dev/null; then
            reply worker_running
        else
            reply worker_stopped
        fi
        ;;
    recover)
        # Used after an interrupted worker. The job already exists in this app session.
        if [ -f wait.jsonl ]; then
            error_json already_finished
        elif ! own_job || ! same_session; then
            error_json app_changed
        elif ! cli_path; then
            error_json cli_missing
        elif /bin/mkdir recovery 2>/dev/null; then
            /usr/bin/nohup /bin/sh ./bridge.sh recover_wait </dev/null >>bridge.stderr 2>&1 &
            printf '%s\n' "$!" > worker.pid
            reply submitted
        else
            error_json recovery_exists
        fi
        ;;
    recover_wait)
        if own_job && same_session && cli_path; then
            "$cli" wait "$job" --out output > recovery/wait.part 2>/dev/null
        else
            error_json app_changed > recovery/wait.part
        fi
        publish_wait recovery/wait.part
        ;;
    cancel)
        if [ -f wait.jsonl ]; then
            error_json already_finished
        elif ! own_job || ! same_session; then
            error_json app_changed
        elif ! cli_path; then
            error_json cli_missing
        else
            route=$("$cli" status 2>/dev/null | /usr/bin/plutil -type computing_on - 2>/dev/null)
            if [ "$route" = "(any)" ] && same_session; then
                "$cli" cancel "$job" 2>/dev/null
            else
                error_json app_changed
            fi
        fi
        ;;
    *) error_json usage ;;
esac
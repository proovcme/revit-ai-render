#!/bin/sh
umask 077

case "$1" in
    launch)
        if [ -d submitted ]; then
            printf '{"state":"submitted"}\n'
            exit 0
        fi
        py=$(/bin/cat python-path.txt) || exit 1
        if [ ! -x "$py" ]; then
            printf '{"state":"failed","code":"mflux-settings"}\n'
            exit 0
        fi
        /bin/mkdir submitted || exit 1
        /usr/bin/nohup "$py" "$PWD/mflux-worker.py" "$PWD" </dev/null >worker.log 2>&1 &
        printf '%s\n' "$!" > submitted/pid
        printf '{"state":"submitted"}\n'
        ;;
    probe)
        if [ ! -d submitted ]; then
            printf '{"state":"not_submitted"}\n'
            exit 0
        fi
        if [ -f state.json ]; then
            /bin/cat state.json
            printf '\n'
        fi
        pid=$(/bin/cat submitted/pid 2>/dev/null)
        case "$pid" in
            ''|*[!0-9]*) ;;
            *)
                owner=$(/bin/ps -p "$pid" -o command= 2>/dev/null)
                case "$owner" in
                    *"$PWD/mflux-worker.py $PWD") printf '{"state":"worker_alive"}\n'; exit 0 ;;
                esac
                ;;
        esac
        printf '{"state":"worker_lost"}\n'
        ;;
    cancel)
        [ -d submitted ] || exit 1
        /bin/mkdir -p cancel-requested
        printf '{"state":"cancel_requested"}\n'
        ;;
    *) exit 1 ;;
esac

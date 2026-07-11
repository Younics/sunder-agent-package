namespace Sunder.Package.Agent.Execution.Docker;

internal static class DockerFileOperationScript
{
    internal const string Protocol = "__SUNDER_FILE_PROTOCOL_V1__";

    internal const string Content = """
        protocol='__SUNDER_FILE_PROTOCOL_V1__'

        emit_error() {
            printf '%s|error|%s\n' "$protocol" "$1"
            exit "$2"
        }

        operation=$1
        candidate=$2
        range_requested=$3
        offset=$4
        limit=$5
        option=$6
        expected_hash=$7
        shift 7
        temporary=
        trap '[ -z "$temporary" ] || rm -f "$temporary"' EXIT

        if resolved=$(realpath / 2>/dev/null) && [ "$resolved" = / ]; then
            canonicalizer=realpath
        elif resolved=$(readlink -f / 2>/dev/null) && [ "$resolved" = / ]; then
            canonicalizer='readlink -f'
        else
            emit_error file-path-unresolvable 76
        fi

        canonicalize() {
            if [ "$canonicalizer" = realpath ]; then
                realpath "$1" 2>/dev/null
            else
                readlink -f "$1" 2>/dev/null
            fi
        }

        resolve_candidate() {
            if [ -e "$candidate" ]; then
                canonicalize "$candidate"
                return
            fi
            if [ -L "$candidate" ]; then
                return 1
            fi

            probe=$candidate
            suffix=
            while [ ! -e "$probe" ]; do
                if [ -L "$probe" ] || [ "$probe" = / ]; then
                    return 1
                fi
                name=${probe##*/}
                suffix=/$name$suffix
                probe=${probe%/*}
                [ -n "$probe" ] || probe=/
            done

            physical_parent=$(canonicalize "$probe") || return 1
            printf '%s%s\n' "$physical_parent" "$suffix"
        }

        physical=$(resolve_candidate) || emit_error file-path-unresolvable 76
        case "$physical" in
            /*) ;;
            *) emit_error file-path-unresolvable 76 ;;
        esac

        inside=false
        [ "$#" -gt 0 ] || emit_error file-outside-scope 77
        for workspace_root do
            [ -d "$workspace_root" ] || emit_error file-path-unresolvable 76
            physical_root=$(canonicalize "$workspace_root") || emit_error file-path-unresolvable 76
            case "$physical" in
                "$physical_root"|"$physical_root"/*) inside=true ;;
            esac
        done
        [ "$inside" = true ] || emit_error file-outside-scope 77

        verify_expected_hash() {
            [ "$expected_hash" = - ] && return 0
            [ -f "$1" ] || emit_error file-content-changed 84
            if command -v sha256sum >/dev/null 2>&1; then
                hash_output=$(sha256sum "$1" 2>/dev/null) || emit_error file-content-changed 84
            elif command -v shasum >/dev/null 2>&1; then
                hash_output=$(shasum -a 256 "$1" 2>/dev/null) || emit_error file-content-changed 84
            else
                emit_error file-hash-unavailable 85
            fi
            actual_hash=${hash_output%% *}
            [ "$actual_hash" = "$expected_hash" ] || emit_error file-content-changed 84
        }

        case "$operation" in
            read)
                if [ -d "$physical" ]; then
                    printf '%s|directory\n' "$protocol"
                    LC_ALL=C ls -1A "$physical" || exit 78
                    exit 0
                fi
                [ -e "$physical" ] || emit_error file-not-found 74
                [ -f "$physical" ] || emit_error file-not-regular 79
                [ -r "$physical" ] || emit_error file-read-failed 78
                command -v od >/dev/null 2>&1 || emit_error file-read-failed 78
                command -v awk >/dev/null 2>&1 || emit_error file-read-failed 78

                hex=$(LC_ALL=C od -An -tx1 -N 8192 "$physical" 2>/dev/null) || emit_error file-read-failed 78
                case " $hex " in
                    *' 00 '*) emit_error file-binary 75 ;;
                esac

                total=$(LC_ALL=C awk 'END { print NR + 0 }' "$physical" 2>/dev/null) || emit_error file-read-failed 78
                case "$total" in
                    ''|*[!0-9]*) emit_error file-read-failed 78 ;;
                esac

                if [ "$range_requested" = 1 ]; then
                    case "$offset:$limit" in
                        *[!0-9:]*|:*|*:) emit_error file-range-invalid 80 ;;
                    esac
                    [ "$offset" -ge 1 ] && [ "$limit" -ge 1 ] && [ "$limit" -le 2000 ] || emit_error file-range-invalid 80
                    if [ "$offset" -gt "$total" ] && ! { [ "$offset" -eq 1 ] && [ "$total" -eq 0 ]; }; then
                        printf '%s|error|file-range-out-of-bounds|%s\n' "$protocol" "$total"
                        exit 81
                    fi
                    end=$((offset + limit - 1))
                    [ "$end" -le "$total" ] || end=$total
                    [ "$end" -lt "$total" ] && truncated=1 || truncated=0
                    printf '%s|file|%s|%s|%s|%s|1\n' "$protocol" "$offset" "$end" "$total" "$truncated"
                    LC_ALL=C awk -v start="$offset" -v finish="$end" 'NR >= start && NR <= finish { print }' "$physical" || exit 78
                else
                    printf '%s|file|1|%s|%s|0|0\n' "$protocol" "$total" "$total"
                    cat "$physical" || exit 78
                fi
                ;;
            write)
                if [ "$option" = 0 ] && { [ -e "$candidate" ] || [ -L "$candidate" ]; }; then
                    emit_error file-exists 73
                fi
                [ ! -d "$physical" ] || emit_error file-not-regular 79
                parent=${physical%/*}
                [ -n "$parent" ] || parent=/
                mkdir -p "$parent" || emit_error docker-write-failed 82
                temporary=$(mktemp "$parent/.sunder-write.XXXXXX") || emit_error docker-write-failed 82
                if ! cat > "$temporary"; then
                    rm -f "$temporary"
                    emit_error docker-write-failed 82
                fi
                if command -v sync >/dev/null 2>&1; then
                    sync "$temporary" 2>/dev/null || sync 2>/dev/null || {
                        rm -f "$temporary"
                        emit_error docker-write-failed 82
                    }
                fi

                latest=$(resolve_candidate) || {
                    rm -f "$temporary"
                    emit_error file-path-unresolvable 76
                }
                [ "$latest" = "$physical" ] || {
                    rm -f "$temporary"
                    emit_error file-content-changed 84
                }
                if [ "$option" = 0 ] && { [ -e "$candidate" ] || [ -L "$candidate" ]; }; then
                    rm -f "$temporary"
                    emit_error file-exists 73
                fi
                verify_expected_hash "$physical"
                if [ "$option" = 0 ]; then
                    if ! ln "$temporary" "$physical" 2>/dev/null; then
                        rm -f "$temporary"
                        emit_error file-exists 73
                    fi
                    rm -f "$temporary"
                    temporary=
                else
                    mv -f "$temporary" "$physical" || {
                        rm -f "$temporary"
                        emit_error docker-write-failed 82
                    }
                    temporary=
                fi
                printf '%s|ok|file-written\n' "$protocol"
                ;;
            delete)
                if [ -L "$candidate" ] || [ -f "$candidate" ]; then
                    verify_expected_hash "$physical"
                    rm -f "$candidate" || emit_error docker-delete-failed 83
                    printf '%s|ok|file-deleted\n' "$protocol"
                elif [ -d "$candidate" ]; then
                    if [ "$option" = 1 ]; then
                        rm -rf "$candidate" || emit_error docker-delete-failed 83
                    else
                        rmdir "$candidate" || emit_error docker-delete-failed 83
                    fi
                    printf '%s|ok|directory-deleted\n' "$protocol"
                elif [ -e "$candidate" ]; then
                    emit_error file-not-regular 79
                else
                    emit_error path-not-found 74
                fi
                ;;
            exists)
                if [ -f "$physical" ]; then
                    printf '%s|exists|file\n' "$protocol"
                elif [ -d "$physical" ]; then
                    printf '%s|exists|directory\n' "$protocol"
                elif [ -e "$candidate" ] || [ -L "$candidate" ]; then
                    emit_error file-not-regular 79
                else
                    printf '%s|missing\n' "$protocol"
                fi
                ;;
            *) emit_error file-read-failed 78 ;;
        esac
        """;
}

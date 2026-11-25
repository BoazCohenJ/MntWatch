#!/bin/sh
# MntWatch helper. MntWatch.exe starts this as root inside the WSL distro and drives it over stdin, one
# tab-separated command per line. It only uses POSIX sh, coreutils/busybox, awk and mount, so it runs on
# Ubuntu, Debian, Alpine and friends without installing anything.
#
#   MOUNT  <target> <windows path> <options>   overmount a Windows folder with inode caching
#   UMOUNT <target>
#   N      <path>                              queue a path to nudge
#   F      <seq>                               nudge everything queued, then answer ACK <seq> <count>
#   SWEEP  <target> <seq>                      queue every path under target (after a lost event), then flush
#   STAT   <path>                              answer STAT <size> as Linux currently sees it (self-test)
#   PING   <x>                                 answer PONG <x>
#   SCAN                                       look for other WSL mount namespaces (see below)
#
# WSL keeps one mount namespace for programs started from elevated (administrator) Windows processes and one for
# the rest, so a mount made on one side is invisible on the other (issue #2). The helper finds the other
# namespaces by their WSL init processes and repeats every mount, nudge and unmount there through nsenter.
# It reports how many it bridges as NS <count>, or NS nonsenter when one exists but nsenter is missing.
#
# When stdin closes (MntWatch exited or was killed) every folder this helper mounted is unmounted again.
rm -f "$0"
VERSION=1
T=$(printf '\t')
B=$(mktemp /tmp/mntwatch.XXXXXX) || exit 1
MOUNTS="$B.mounts"
NS="$B.ns"
: > "$MOUNTS"
: > "$NS"
SELF_NS=$(stat -L -c %i /proc/self/ns/mnt 2>/dev/null)
if command -v nsenter >/dev/null 2>&1; then NSENTER=nsenter
elif busybox --list 2>/dev/null | grep -qx nsenter; then NSENTER="busybox nsenter"
else NSENTER=""
fi
NS_SAID=0

# Mode-grouped chmod of the "<type> <mode> <path>" lines in $1; run in this namespace and in each other one.
cat > "$B.nudge" <<'NUDGE'
for mode in $(cut -d' ' -f2 "$1" | sort -u); do
  grep "^[0-9a-f]* $mode " "$1" | cut -d' ' -f3- | tr '\n' '\0' | xargs -0 -r chmod "$mode" 2>/dev/null
done
NUDGE

say() { printf '%s\n' "$*"; }

# Runs a command in every other bridged namespace: in_ns <command> [args...]
in_ns() {
  [ -s "$NS" ] || return 0
  while read -r _ino pid; do
    $NSENTER -t "$pid" -m -- "$@" < /dev/null
  done < "$NS"
}

cleanup() {
  while IFS="$T" read -r m _w _o; do
    umount "$m" 2>/dev/null || umount -l "$m" 2>/dev/null
    in_ns sh -c 'umount "$1" 2>/dev/null || umount -l "$1" 2>/dev/null' x "$m"
  done < "$MOUNTS"
  rm -f "$B" "$B".*
}

# The other WSL mount namespaces, as "<namespace inode> <lowest pid in it>". WSL's own processes (init, its
# plan9 server, session leaders, relays) all run the /init binary, which keeps out the private namespaces of
# systemd services and containers: those have no WSL process in them.
scan() {
  [ -n "$SELF_NS" ] || return 0
  pids=$(ls -l /proc/[0-9]*/exe 2>/dev/null | awk '$NF == "/init" { split($(NF-2), a, "/"); print a[3] }')
  [ -n "$pids" ] || return 0
  found=$(for p in $pids; do stat -L -c "%i $p" "/proc/$p/ns/mnt" 2>/dev/null; done |
    awk -v self="$SELF_NS" '$1 != self && (!($1 in m) || $2 + 0 < m[$1] + 0) { m[$1] = $2 } END { for (i in m) print i, m[i] }' | sort)
  if [ -z "$found" ]; then
    if [ -s "$NS" ] || [ "$NS_SAID" != 0 ]; then : > "$NS"; NS_SAID=0; say "NS${T}0"; fi
    return 0
  fi
  if [ -z "$NSENTER" ]; then
    [ "$NS_SAID" = nonsenter ] || { NS_SAID=nonsenter; say "NS${T}nonsenter"; }
    return 0
  fi
  printf '%s\n' "$found" > "$NS.new"
  # Bring namespaces seen for the first time up to date with every folder mounted so far.
  cut -d' ' -f1 "$NS.new" | while read -r ino; do
    grep -q "^$ino " "$NS" && continue
    pid=$(grep "^$ino " "$NS.new" | cut -d' ' -f2)
    while IFS="$T" read -r m w o; do
      $NSENTER -t "$pid" -m -- mount -t drvfs "$w" "$m" -o "$o" < /dev/null 2>/dev/null
    done < "$MOUNTS"
  done
  mv "$NS.new" "$NS"
  n=$(wc -l < "$NS" | tr -d ' ')
  [ "$NS_SAID" = "$n" ] || { NS_SAID=$n; say "NS${T}$n"; }
}
trap 'cleanup; exit 0' HUP INT TERM

# Writing each path's own mode back makes the kernel fetch fresh attributes from Windows (size, mtime) and
# raise IN_ATTRIB for it, which file watchers report as a change. Nothing on the Windows side changes.
# Symlinks are skipped because chmod follows them and would change the target. Paths that no longer exist
# make stat fail, which still drops the stale directory entry.
flush() {
  n=0
  if [ -s "$B" ]; then
    n=$(wc -l < "$B")
    tr '\n' '\0' < "$B" | xargs -0 -r stat -c '%f %a %n' 2>/dev/null | grep -v '^a[0-9a-f][0-9a-f][0-9a-f] ' > "$B.m"
    sh "$B.nudge" "$B.m" < /dev/null
    in_ns sh "$B.nudge" "$B.m"
    : > "$B"
  fi
  say "ACK${T}$1${T}$n"
}

do_mount() {
  if out=$(mount -t drvfs "$2" "$1" -o "$3" 2>&1); then
    printf '%s\t%s\t%s\n' "$1" "$2" "$3" >> "$MOUNTS"
    in_ns mount -t drvfs "$2" "$1" -o "$3" 2>/dev/null
    say "OK${T}MOUNT${T}$1"
  else
    say "ERR${T}MOUNT${T}$1${T}$(printf '%s' "$out" | tr '\t\n' '  ')"
  fi
}

do_umount() {
  how=""
  if ! umount "$1" 2>/dev/null; then
    # Something still has its working directory inside; detach now, the kernel finishes once it's free.
    umount -l "$1" 2>/dev/null && how="${T}lazy"
  fi
  in_ns sh -c 'umount "$1" 2>/dev/null || umount -l "$1" 2>/dev/null' x "$1"
  awk -F "$T" -v m="$1" '$1 != m' "$MOUNTS" > "$MOUNTS.new"
  mv "$MOUNTS.new" "$MOUNTS"
  say "OK${T}UMOUNT${T}$1$how"
}

say "HELLO${T}$VERSION${T}$(uname -r)"
awk -v t="$T" '$4 ~ /aname=drvfs/ || $3 == "drvfs" { print "DRVFS" t $2 t $4 }' /proc/mounts
say "READY"

while IFS= read -r line; do
  cmd=${line%%"$T"*}
  rest=${line#*"$T"}
  case "$cmd" in
    N) printf '%s\n' "$rest" >> "$B" ;;
    F) flush "$rest" ;;
    MOUNT)
      target=${rest%%"$T"*}; rest=${rest#*"$T"}
      win=${rest%%"$T"*}; opts=${rest#*"$T"}
      do_mount "$target" "$win" "$opts" ;;
    UMOUNT) do_umount "$rest" ;;
    SWEEP)
      target=${rest%%"$T"*}; seq=${rest#*"$T"}
      find "$target" -xdev -print >> "$B" 2>/dev/null
      flush "$seq" ;;
    STAT) say "STAT${T}$(stat -c %s "$rest" 2>/dev/null || echo -1)" ;;
    PING) say "PONG${T}$rest" ;;
    SCAN) scan ;;
  esac
done
cleanup

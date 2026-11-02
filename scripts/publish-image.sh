#!/usr/bin/env bash
# Образ в Docker Hub: проверка уязвимостей, затем публикация с SBOM и происхождением — тег X.Y.Z; самая новая в ветке X.Y — ещё X.Y, самая новая вообще — latest.
#   docker login && ./scripts/publish-image.sh 1.0.1
set -euo pipefail

version="${1:-}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Версия X.Y.Z: ./scripts/publish-image.sh 1.0.1" >&2; exit 1; }

image="danreero/kerio_cert_manager"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build=(docker buildx build --pull -f "$root/KerioCertManager.Api/Dockerfile" --build-arg "VERSION=$version" "$root")

"${build[@]}" --load -t "$image:$version"

if docker scout version >/dev/null 2>&1; then
    docker scout cves --only-severity critical,high --only-fixed --exit-code "$image:$version"
else
    echo "⚠ Docker Scout не установлен — проверка уязвимостей пропущена, её сделает релиз в GitHub." >&2
fi

tags=(-t "$image:$version")
newest() { { echo "$version"; git -C "$root" tag --list "$1" | sed 's/^v//'; } | sort -V | tail -n 1; }

if [[ "$(newest "v${version%.*}.*")" == "$version" ]]; then
    tags+=(-t "$image:${version%.*}")
fi

if [[ "$(newest 'v[0-9]*')" == "$version" ]]; then
    tags+=(-t "$image:latest")
fi

"${build[@]}" --sbom=true --provenance=mode=max --push "${tags[@]}"

echo "Опубликовано: ${tags[*]/#-t/}" | tr -s " "

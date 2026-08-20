#!/bin/bash
# update_engine.sh

set -e  # остановиться если что-то пошло не так

echo "Fetching latest RobustToolbox tags..."
cd RobustToolbox
git fetch --tags origin

# Берём последний тег вида v288.x.x
STR_LatestTag_iR=$(git tag --sort=-version:refname | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | head -1)

echo "Latest tag: $STR_LatestTag_iR"
git checkout "$STR_LatestTag_iR"

cd ..
git add RobustToolbox
git commit -m "Bump RobustToolbox to $STR_LatestTag_iR"

echo "Done! RobustToolbox updated to $STR_LatestTag_iR"
echo "Don't forget to: git push && dotnet build"

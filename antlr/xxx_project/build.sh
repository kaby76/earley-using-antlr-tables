# Generated from trgen 0.23.27
set -e
if [ -f transformGrammar.py ]; then python3 transformGrammar.py ; fi

version=`dotnet trxml2 Other.csproj | fgrep 'PackageReference/@Version' | awk -F= '{print $2}'`

antlr4 -v $version -encoding utf-8 -Dlanguage=CSharp -atn  Ex.g4

dotnet restore Test.csproj
dotnet build Test.csproj

exit 0

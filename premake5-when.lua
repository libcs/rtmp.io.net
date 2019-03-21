rtmp_version = "1.0"

libs = { "System" }

solution "rtmp"
    kind "ConsoleApp"
    language "C#"
    platforms { "x64" }
    configurations { "Debug", "Release" }
    links { libs }
    configuration "Debug"
        symbols "On"
        defines { "DEBUG" }
    configuration "Release"
        optimize "Speed"
        
project "test"
    files { "test.cs", "shared_h.cs" }
    links { "rtmp" }

project "rtmp"
    kind "SharedLib"
    defines { "RTMP" }
    files { "rtmp.cs" }

if not os.istarget "windows" then
else

    -- Windows
    newaction
    {
        trigger     = "solution",
        description = "Create and open the rtmp.io.net solution",
        execute = function ()
            os.execute "premake5 vs2015"
            os.execute "start rtmp.sln"
        end
    }

end

newaction
{
    trigger     = "clean",
    description = "Clean all build files and output",
    execute = function ()
        files_to_delete = 
        {
            "Makefile",
            "packages.config",
            "*.make",
            "*.txt",
            "*.zip",
            "*.tar.gz",
            "*.db",
            "*.opendb",
            "*.csproj",
            "*.csproj.user",
            "*.sln",
            "*.xcodeproj",
            "*.xcworkspace"
        }
        directories_to_delete = 
        {
            "obj",
            "ipch",
            "bin",
            ".vs",
            "Debug",
            "Release",
            "release",
            "packages",
            "cov-int",
            "docs",
            "xml"
        }
        for i,v in ipairs( directories_to_delete ) do
          os.rmdir( v )
        end
        if not os.is "windows" then
            os.execute "find . -name .DS_Store -delete"
            for i,v in ipairs( files_to_delete ) do
              os.execute( "rm -f " .. v )
            end
        else
            for i,v in ipairs( files_to_delete ) do
              os.execute( "del /F /Q  " .. v )
            end
        end

    end
}

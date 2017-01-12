# tmp-qbscript-linter
(TEMP HACK) check a compiled script file for common errors.

Qb Scripts are extremely hard to debug, as errors in them commonly crash the script
interpreter thread.  This tool aims to mitigate that.  However, it does not truly
parse the compiled file, so false negatives are possible: errors can be present and
undetected even if they are of a known class.

Usage: qblint \<file 1\> \<file 2\> ...

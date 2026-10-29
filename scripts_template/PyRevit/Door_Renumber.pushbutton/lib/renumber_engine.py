# Helper engine module for door renumbering
import os
import sys

def get_renumber_summary(count):
    sep = os.sep
    return "Analyzed {0} door elements using OS separator '{1}'.".format(count, sep)

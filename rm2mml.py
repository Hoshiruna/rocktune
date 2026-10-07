# SPDX-License-Identifier: MIT
"""Run the importer without installing a package or third-party dependencies."""

import sys

from rockman2_mml.cli import main


if __name__ == "__main__":
    sys.exit(main())

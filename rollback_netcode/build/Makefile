# Builds  librollbackfbneo.a  =  core (state_ring, ggpo_bridge, nat_punch, relay, port_map)
#                             +  fbneo_host, ram_probe
#                             +  vendored libggpo
#
# This .a is linked into the FBNeo MinGW build by
#   patches/fbneo/0002-mingw-link-rollback.diff
#
# Run it from the SAME MSYS2 / MinGW shell you use for `make mingw` in fbneo/,
# from THIS directory (rollback_netcode/build):
#
#   make                     # 32-bit  (matches `make mingw`)
#   make BUILD_X64_EXE=1      # 64-bit  (matches `make mingw BUILD_X64_EXE=1`)
#   make clean
#
# IMPORTANT: every path here is RELATIVE. The repo lives under a directory with
# a space ("Rollback Netcode") and GNU make cannot have spaces in target or
# prerequisite names.
#
# Our files are built -DUNICODE (to match FBNeo's TCHAR world - _T(), bprintf).
# The vendored libggpo files are built WITHOUT it: they use the ANSI Win32 API
# (GetEnvironmentVariable, MessageBoxA, ...) and must not get the -W variants.
#
# Overridable: CXX, AR, CXXFLAGS, FBNEO (relative path to the FBNeo checkout).

CXX   ?= g++
AR    ?= ar

ROOT  := ..
FBNEO ?= ../../fbneo
GGPO  := $(ROOT)/third_party/ggpo/src

ARCHFLAG := -m32
ifdef BUILD_X64_EXE
  ARCHFLAG := -m64
endif

# burnint.h and its leaf headers live under src/burn and src/burn/devices;
# <tchar.h> comes from the MinGW system headers on Windows.
INC := -I$(ROOT)/core \
       -I$(GGPO)/include \
       -I$(GGPO)/lib/ggpo \
       -I$(FBNEO)/src/burn \
       -I$(FBNEO)/src/burn/devices

CXXFLAGS ?= -O2 -pipe
COMMON   := $(CXXFLAGS) $(ARCHFLAG) -std=gnu++17 -fno-strict-aliasing -fpermissive \
            -Wno-write-strings -Wno-unused-parameter -DBUILD_WIN32 $(INC)

OBJDIR := obj
LIB    := librollbackfbneo.a

OUR_SRC :=
OUR_SRC += $(ROOT)/core/state_ring.cpp
OUR_SRC += $(ROOT)/core/ggpo_bridge.cpp
OUR_SRC += $(ROOT)/core/nat_punch.cpp
OUR_SRC += $(ROOT)/core/relay.cpp
OUR_SRC += $(ROOT)/core/port_map.cpp
OUR_SRC += $(ROOT)/fbneo/fbneo_host.cpp
OUR_SRC += $(ROOT)/fbneo/ram_probe.cpp
OUR_SRC += $(ROOT)/fbneo/match_score.cpp
OUR_SRC += $(ROOT)/fbneo/overlay.cpp
OUR_SRC += $(ROOT)/fbneo/hud.cpp

GGPO_SRC :=
GGPO_SRC += $(GGPO)/lib/ggpo/bitvector.cpp
GGPO_SRC += $(GGPO)/lib/ggpo/game_input.cpp
GGPO_SRC += $(GGPO)/lib/ggpo/input_queue.cpp
GGPO_SRC += $(GGPO)/lib/ggpo/log.cpp
GGPO_SRC += $(GGPO)/lib/ggpo/main.cpp
GGPO_SRC += $(GGPO)/lib/ggpo/poll.cpp
GGPO_SRC += $(GGPO)/lib/ggpo/sync.cpp
GGPO_SRC += $(GGPO)/lib/ggpo/timesync.cpp
GGPO_SRC += $(GGPO)/lib/ggpo/platform_windows.cpp
GGPO_SRC += $(GGPO)/lib/ggpo/network/udp.cpp
GGPO_SRC += $(GGPO)/lib/ggpo/network/udp_proto.cpp
GGPO_SRC += $(GGPO)/lib/ggpo/backends/p2p.cpp
GGPO_SRC += $(GGPO)/lib/ggpo/backends/spectator.cpp
GGPO_SRC += $(GGPO)/lib/ggpo/backends/synctest.cpp

# All basenames are unique across the two sets.
OUR_OBJ  := $(addprefix $(OBJDIR)/,$(notdir $(OUR_SRC:.cpp=.o)))
GGPO_OBJ := $(addprefix $(OBJDIR)/,$(notdir $(GGPO_SRC:.cpp=.o)))
OBJ      := $(OUR_OBJ) $(GGPO_OBJ)

vpath %.cpp $(ROOT)/core $(ROOT)/fbneo \
            $(GGPO)/lib/ggpo $(GGPO)/lib/ggpo/network $(GGPO)/lib/ggpo/backends

.PHONY: all clean
all: $(LIB)

$(LIB): $(OBJ)
	$(AR) rcs $@ $^
	@echo "  ->  $@   ($(words $(OBJ)) objects, $(ARCHFLAG))"

# our code: UNICODE, like FBNeo
$(OUR_OBJ): $(OBJDIR)/%.o : %.cpp | $(OBJDIR)
	$(CXX) $(COMMON) -DUNICODE -D_UNICODE -c $< -o $@

# vendored libggpo: ANSI Win32 API
$(GGPO_OBJ): $(OBJDIR)/%.o : %.cpp | $(OBJDIR)
	$(CXX) $(COMMON) -c $< -o $@

$(OBJDIR):
	@mkdir -p $(OBJDIR)

clean:
	@rm -rf $(OBJDIR) $(LIB)

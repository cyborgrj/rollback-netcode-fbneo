# Builds  librollbackfbneo.a  =  core (state_ring, ggpo_bridge)
#                             +  fbneo_host
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
# prerequisite names - an absolute path would break the build.
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

# Match FBNeo's win32 build: UNICODE TCHAR world.
DEFS := -DUNICODE -D_UNICODE -DBUILD_WIN32

CXXFLAGS ?= -O2 -pipe
CXXFLAGS += $(ARCHFLAG) -std=gnu++17 -fno-strict-aliasing -fpermissive \
            -Wno-write-strings -Wno-unused-parameter $(DEFS) $(INC)

OBJDIR := obj
LIB    := librollbackfbneo.a

OUR_SRC := \
	$(ROOT)/core/state_ring.cpp \
	$(ROOT)/core/ggpo_bridge.cpp \
	$(ROOT)/fbneo/fbneo_host.cpp

GGPO_SRC := \
	$(GGPO)/lib/ggpo/bitvector.cpp \
	$(GGPO)/lib/ggpo/game_input.cpp \
	$(GGPO)/lib/ggpo/input_queue.cpp \
	$(GGPO)/lib/ggpo/log.cpp \
	$(GGPO)/lib/ggpo/main.cpp \
	$(GGPO)/lib/ggpo/poll.cpp \
	$(GGPO)/lib/ggpo/sync.cpp \
	$(GGPO)/lib/ggpo/timesync.cpp \
	$(GGPO)/lib/ggpo/platform_windows.cpp \
	$(GGPO)/lib/ggpo/network/udp.cpp \
	$(GGPO)/lib/ggpo/network/udp_proto.cpp \
	$(GGPO)/lib/ggpo/backends/p2p.cpp \
	$(GGPO)/lib/ggpo/backends/spectator.cpp \
	$(GGPO)/lib/ggpo/backends/synctest.cpp

# All basenames are unique across the two sets.
OBJ := $(addprefix $(OBJDIR)/,$(notdir $(OUR_SRC:.cpp=.o) $(GGPO_SRC:.cpp=.o)))

vpath %.cpp $(ROOT)/core $(ROOT)/fbneo \
            $(GGPO)/lib/ggpo $(GGPO)/lib/ggpo/network $(GGPO)/lib/ggpo/backends

.PHONY: all clean
all: $(LIB)

$(LIB): $(OBJ)
	$(AR) rcs $@ $^
	@echo "  ->  $(OBJDIR)/../$@   ($(words $(OBJ)) objects, $(ARCHFLAG))"

$(OBJDIR)/%.o: %.cpp | $(OBJDIR)
	$(CXX) $(CXXFLAGS) -c $< -o $@

$(OBJDIR):
	@mkdir -p $(OBJDIR)

clean:
	@rm -rf $(OBJDIR) $(LIB)

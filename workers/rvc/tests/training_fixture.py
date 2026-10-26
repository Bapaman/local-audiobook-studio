"""Tiny real CPU optimizers, never a substitute for an RVC quality test."""
import random
import numpy as np
import torch


def seed_all(seed):
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    torch.set_num_threads(2)


def make_state(state_type):
    generator = torch.nn.Sequential(torch.nn.Linear(3, 2), torch.nn.Dropout(.15))
    discriminator = torch.nn.Linear(2, 1)
    optimizer_g = torch.optim.AdamW(generator.parameters(), lr=.001, betas=(.8, .99), eps=1e-9)
    optimizer_d = torch.optim.AdamW(discriminator.parameters(), lr=.002, betas=(.8, .99), eps=1e-9)
    scheduler_g = torch.optim.lr_scheduler.ExponentialLR(optimizer_g, gamma=.9)
    scheduler_d = torch.optim.lr_scheduler.ExponentialLR(optimizer_d, gamma=.8)
    scaler = torch.amp.GradScaler("cpu", enabled=False)
    return state_type(generator, discriminator, optimizer_g, optimizer_d,
                      scheduler_g, scheduler_d, scaler, sampler_seed=1234)


def update(state):
    state.generator.train()
    state.discriminator.train()
    inputs = torch.randn(4, 3) + random.random() + float(np.random.rand())
    generated = state.generator(inputs)
    state.optimizer_d.zero_grad()
    loss_d = state.discriminator(generated.detach()).square().mean()
    loss_d.backward()
    state.optimizer_d.step()
    state.optimizer_g.zero_grad()
    loss_g = state.discriminator(generated).square().mean()
    loss_g.backward()
    state.optimizer_g.step()
    return float(loss_g.detach()), float(loss_d.detach())


def step_schedulers(state):
    state.scheduler_g.step()
    state.scheduler_d.step()
